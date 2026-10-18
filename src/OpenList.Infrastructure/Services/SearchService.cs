using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 搜索索引服务。通过 <see cref="IFileSystemService"/> 遍历文件系统，并把条目持久化到
/// <c>x_search_index</c> 表（“数据库”索引后端）。查询分发由 <c>search_index</c> 设置决定：
/// <list type="bullet">
/// <item><c>none</c> — 不使用索引；<see cref="SearchAsync"/> 委托给实时文件系统。</item>
/// <item><c>database</c> — <see cref="SearchAsync"/> 查询已持久化的索引行。</item>
/// <item><c>bleve</c> — 未实现；回退为文件系统搜索。</item>
/// </list>
/// 对应 Go 参考实现 <c>server/handles/search.go</c>。
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly IFileSystemService _fs;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly IObjsUpdateHooks _hooks;
    private readonly object _lock = new();
    private IndexProgress _progress = new();
    private CancellationTokenSource? _cts;
    private DateTimeOffset? _lastDoneTime;

    // 增量更新按父目录串行化（镜像 Go internal/search/update_lock.go）：
    // 同一父目录的更新排队执行，不同父目录可并行，避免并发差集写入冲突。
    private static readonly object UpdateLocksGate = new();
    private static readonly Dictionary<string, UpdateLock> UpdateLocks = new();

    /// <summary>
    /// 创建搜索索引服务实例，并向 <see cref="IObjsUpdateHooks"/> 注册增量更新订阅，
    /// 使文件系统写操作后索引自动保持新鲜（受 <c>auto_update_index</c> 设置控制）。
    /// </summary>
    /// <param name="fs">文件系统服务，用于遍历条目与实时搜索。</param>
    /// <param name="scopes">作用域工厂，用于按需创建数据库上下文。</param>
    /// <param name="hooks">文件对象更新钩子注册表，用于订阅增量索引。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public SearchService(IFileSystemService fs, IServiceScopeFactory scopes, IObjsUpdateHooks hooks, TimeProvider? time = null)
    {
        _fs = fs;
        _scopes = scopes;
        _hooks = hooks;
        _time = time ?? TimeProvider.System;
        _hooks.Subscribe(UpdateDirectoryAsync, RemovePathAsync);
    }

    /// <summary>
    /// 全量重建索引（先清空再遍历重建）。
    /// </summary>
    public Task<Resp> BuildIndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct = default)
        => StartAsync(paths, maxDepth, isUpdate: false, ct);

    /// <summary>
    /// 增量更新索引（仅删除并重建本次遍历根节点下的条目）。
    /// </summary>
    public Task<Resp> UpdateIndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct = default)
        => StartAsync(paths, maxDepth, isUpdate: true, ct);

    /// <summary>
    /// 启动索引构建任务（后台一次性执行）。已有构建在运行则返回冲突。
    /// </summary>
    private Task<Resp> StartAsync(IReadOnlyList<string> paths, int maxDepth, bool isUpdate, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_progress.State == "running")
                return Task.FromResult(Resp.Fail("index build already running", RespCodes.Conflict));

            _cts = new CancellationTokenSource();
            _progress = new IndexProgress
            {
                State = "running",
                StartedAt = _time.GetUtcNow(),
                // 带上一次完成时间，使进度端点保持一致。
                LastDoneTime = _lastDoneTime,
            };
        }

        var effectivePaths = paths.Count == 0 ? new[] { "/" } : paths;
        _ = Task.Run(() => IndexAsync(effectivePaths, maxDepth, isUpdate, _cts.Token), CancellationToken.None);

        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 取消正在进行的索引构建。
    /// </summary>
    public Task<Resp> StopIndexAsync(CancellationToken ct = default)
    {
        lock (_lock) { _cts?.Cancel(); }
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 清空搜索索引（取消进行中的构建并删除全部索引行）。
    /// </summary>
    public async Task<Resp> ClearIndexAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _progress = new IndexProgress { State = "idle", LastDoneTime = _lastDoneTime };
        }
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        // 一次往返删除所有行。
        await db.SearchIndex.ExecuteDeleteAsync(ct);
        await WriteProgressAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 启动时恢复持久化的索引进度（镜像 Go 的 <c>bootstrap.InitIndex</c>）：
    /// 把上次构建的完成时间载入内存，使增量索引在服务重启后仍被允许；
    /// 若上次进程崩溃时留下 <c>running</c> 状态，则置为完成（<c>is_done=true</c>），
    /// 避免索引永远卡在“构建中”而禁用增量更新。
    /// </summary>
    public async Task<Resp> InitIndexAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            var r = await svc.GetAsync("index_progress", ct);
            if (r.Code == 200 && r.Data is not null && !string.IsNullOrWhiteSpace(r.Data.Value))
            {
                using var doc = JsonDocument.Parse(r.Data.Value);
                var root = doc.RootElement;
                DateTimeOffset? lastDone = null;
                if (root.TryGetProperty("last_done_time", out var ldt)
                    && ldt.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(ldt.GetString(), out var parsed))
                {
                    lastDone = parsed;
                }
                bool wasRunning = root.TryGetProperty("is_done", out var done)
                    && done.ValueKind == JsonValueKind.False;
                lock (_lock)
                {
                    if (lastDone is not null)
                        _lastDoneTime = lastDone;
                    // 崩溃遗留的 running 状态恢复为 idle，否则 ShouldAutoUpdateAsync 会因
                    // State=="running" 永远拒绝增量更新。
                    if (wasRunning && string.Equals(_progress.State, "running", StringComparison.Ordinal))
                        _progress.State = "idle";
                }
            }
            return Resp.Ok();
        }
        catch (Exception ex)
        {
            return Resp.Fail($"failed to load index progress: {ex.Message}", RespCodes.InternalError);
        }
    }

    /// <summary>
    /// 获取当前索引构建进度的快照副本。
    /// </summary>
    public Task<Resp<IndexProgress>> GetProgressAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var snapshot = new IndexProgress
            {
                State = _progress.State,
                Indexed = _progress.Indexed,
                Total = _progress.Total,
                StartedAt = _progress.StartedAt,
                FinishedAt = _progress.FinishedAt,
                LastDoneTime = _progress.LastDoneTime,
                Error = _progress.Error,
            };
            return Task.FromResult(Resp<IndexProgress>.Ok(snapshot));
        }
    }

    /// <summary>
    /// 按 <c>search_index</c> 设置分发搜索：数据库后端查询索引表，否则委托给实时文件系统。
    /// 两种路径均依据用户的 BasePath 与元数据权限过滤结果。
    /// </summary>
    public async Task<Resp<SearchResp>> SearchAsync(SearchReq req, User user, CancellationToken ct = default)
    {
        // 根据 search_index 设置决定后端。
        var backend = await GetSettingAsync("search_index", "none");
        if (!string.Equals(backend, "database", StringComparison.OrdinalIgnoreCase))
        {
            // none / bleve（未实现）→ 实时文件系统搜索。
            return await _fs.SearchAsync(req, user, ct);
        }

        return await SearchDatabaseAsync(req, user, ct);
    }

    /// <summary>
    /// 在持久化索引中搜索：按名称 LIKE 过滤，支持递归/非递归范围、访问控制过滤与分页。
    /// 访问控制镜像 Go <c>server/handles/search.go</c> 的 SearchFiltered：
    /// 仅保留位于用户 BasePath 之下、且通过元数据权限（隐藏/读用户/密码）校验的条目。
    /// </summary>
    private async Task<Resp<SearchResp>> SearchDatabaseAsync(SearchReq req, User user, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();

        var parent = NormalizeParent(req.Parent);
        // SQLite 的 LIKE 默认对 ASCII 不区分大小写，这与 Go 的数据库搜索常见场景一致。
        var kw = $"%{EscapeLike(req.Keywords)}%";
        var q = db.SearchIndex.AsNoTracking()
            .Where(e => EF.Functions.Like(e.Name, kw, "\\"));
        if (req.Recursive)
            q = q.Where(e => e.Parent == "/" || e.Parent.StartsWith(parent));
        else
            q = q.Where(e => e.Parent == parent);

        var rows = await q.OrderByDescending(e => e.Id)
            .Select(e => new SearchNode
            {
                Parent = e.Parent,
                Name = e.Name,
                IsDir = e.IsDir,
                Size = e.Size,
                Path = e.Path,
                Type = e.Type,
            })
            .ToListAsync(ct);

        // 一次加载全部 Meta 到内存，供逐条做最近 Meta + 权限校验（避免逐节点 N+1 查询）。
        var metas = await db.Metas.AsNoTracking().ToListAsync(ct);
        foreach (var m in metas) m.Hydrate();
        var metasByPath = metas.ToDictionary(m => NormalizeParent(m.Path), StringComparer.Ordinal);

        // 过滤后重新计算总数与分页（对齐 Go 的 SearchFiltered 分页语义）。
        var filtered = rows.Where(node => CanAccessNode(node, req, user, metasByPath)).ToList();
        var total = filtered.Count;
        var page = Math.Max(1, req.Page);
        var perPage = req.PerPage < 1 ? int.MaxValue : req.PerPage;
        var paged = filtered.Skip((page - 1) * perPage).Take(perPage).ToList();

        return Resp<SearchResp>.Ok(new SearchResp { Content = paged, Total = total });
    }

    /// <summary>
    /// 判断搜索结果节点对当前用户是否可访问。镜像 Go <c>server/handles/search.go</c>
    /// <c>isSearchNodeAccessible</c>：先校验 <paramref name="node"/> 位于用户 BasePath 之下，
    /// 再用最近 Meta 做隐藏/读用户/密码校验；目录节点以自身路径解析 Meta，
    /// 否则挂在目录本身的隐藏/密码 Meta 会被遗漏。
    /// </summary>
    private static bool CanAccessNode(SearchNode node, SearchReq req, User user, Dictionary<string, Meta> metasByPath)
    {
        if (!IsSubPath(user.BasePath, node.Parent))
            return false;
        var nodePath = JoinPath(node.Parent, node.Name);
        var meta = GetNearestMeta(node.IsDir ? nodePath : node.Parent, metasByPath);
        return CanAccess(user, meta, nodePath, req.Password);
    }

    /// <summary>
    /// 综合权限判断（隐藏文件/读用户/免密/密码）。判定逻辑统一收敛在
    /// <see cref="MetaAccess.CanAccess"/>，避免与 fs/list、fs/get 的密码门禁各写一份而走偏。
    /// </summary>
    private static bool CanAccess(User user, Meta? meta, string reqPath, string? password)
        => MetaAccess.CanAccess(user, meta, reqPath, password);

    /// <summary>读用户限制校验，镜像 Go <c>server/common/check.go</c> CanRead。</summary>
    private static bool CanRead(User user, Meta? meta, string path)
        => MetaAccess.CanRead(user, meta, path);

    /// <summary>
    /// 从路径向上找到最近的 Meta（含自身），找不到返回 null。镜像 Go <c>internal/op/meta.go</c> GetNearestMeta。
    /// </summary>
    private static Meta? GetNearestMeta(string path, Dictionary<string, Meta> metasByPath)
    {
        var current = NormalizeParent(path);
        while (true)
        {
            if (metasByPath.TryGetValue(current, out var meta))
                return meta;
            if (current == "/") return null;
            current = ParentPath(current);
        }
    }

    /// <summary>判断 <paramref name="subPath"/> 是否位于 <paramref name="path"/> 之下（含相等），镜像 Go utils.IsSubPath。</summary>
    private static bool IsSubPath(string? path, string subPath)
    {
        var p = FixAndCleanPath(path ?? "/");
        var s = FixAndCleanPath(subPath);
        return p == s || s.StartsWith(AddTrailingSlash(p), StringComparison.Ordinal);
    }

    /// <summary>把路径规整为以 / 开头、无 ./.. 与重复斜杠的标准形式（空视为根目录），镜像 Go utils.FixAndCleanPath。</summary>
    private static string FixAndCleanPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    /// <summary>确保路径以 / 结尾，镜像 Go utils.PathAddSeparatorSuffix。</summary>
    private static string AddTrailingSlash(string path)
        => path.EndsWith('/') ? path : path + "/";

    /// <summary>返回路径的父目录（根目录的父目录仍为根）。</summary>
    private static string ParentPath(string path)
    {
        var cleaned = FixAndCleanPath(path);
        if (cleaned == "/") return "/";
        var idx = cleaned.LastIndexOf('/');
        return idx <= 0 ? "/" : cleaned[..idx];
    }

    /// <summary>
    /// 增量更新单个目录的索引（由 <see cref="IObjsUpdateHooks"/> 触发）。镜像 Go 的
    /// <c>search.Update</c>：把传入的最新条目列表与数据库中该父目录下的既有索引做差集，
    /// 删除已不存在的条目、批量补入新条目。仅在满足以下条件时生效：
    /// <list type="bullet">
    /// <item><c>search_index</c> 后端为 <c>database</c>；</item>
    /// <item><c>auto_update_index</c> 设置为开启；</item>
    /// <item>没有正在进行的全量构建/更新；</item>
    /// <item>索引至少成功构建过一次。</item>
    /// </list>
    /// </summary>
    private async Task UpdateDirectoryAsync(string parent, IReadOnlyList<Obj> entries, CancellationToken ct)
    {
        if (!await ShouldAutoUpdateAsync(parent, ct)) return;

        var normalized = NormalizeParent(parent);
        // 同一父目录的更新串行执行；等待期间可能开始全量构建，故在锁内复查门控。
        using var updateLock = await AcquireUpdateLockAsync(normalized, ct);
        if (!await ShouldAutoUpdateAsync(normalized, ct)) return;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();

        var existing = await db.SearchIndex.AsNoTracking()
            .Where(e => e.Parent == normalized)
            .ToListAsync(ct);
        var nowNames = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

        // 删除索引中存在、但最新列表里已消失的条目。
        var stale = existing.Where(e => !nowNames.Contains(e.Name)).ToList();
        if (stale.Count > 0)
            db.SearchIndex.RemoveRange(stale);

        // 批量补入最新列表中有、但索引里还没有的条目。
        var oldNames = existing.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var toAdd = entries
            .Where(e => !oldNames.Contains(e.Name))
            .Select(e => new SearchIndexEntry
            {
                Parent = normalized,
                Name = e.Name,
                IsDir = e.IsDir,
                Size = e.Size,
                Path = JoinPath(normalized, e.Name),
                Type = e.Type,
            })
            .ToList();
        if (toAdd.Count > 0)
            await db.SearchIndex.AddRangeAsync(toAdd, ct);

        if (stale.Count > 0 || toAdd.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 为给定父目录获取串行化更新锁（返回 <see cref="IDisposable"/>，释放时解锁并在无
    /// 等待者时清理字典项）。镜像 Go <c>internal/search/update_lock.go</c> 的 lockUpdate。
    /// </summary>
    private async Task<IDisposable> AcquireUpdateLockAsync(string parent, CancellationToken ct)
    {
        UpdateLock l;
        lock (UpdateLocksGate)
        {
            if (!UpdateLocks.TryGetValue(parent, out l!))
            {
                l = new UpdateLock();
                UpdateLocks[parent] = l;
            }
            l.Refs++;
        }
        try
        {
            await l.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 等待被取消：撤销引用计数，避免字典项泄漏。
            lock (UpdateLocksGate)
            {
                l.Refs--;
                if (l.Refs == 0) UpdateLocks.Remove(parent);
            }
            throw;
        }
        return new UpdateLockReleaser(l, parent);
    }

    /// <summary>当前持有的父目录锁数量（仅测试用）。</summary>
    internal static int UpdateLockCount
    {
        get { lock (UpdateLocksGate) { return UpdateLocks.Count; } }
    }

    /// <summary>单个父目录的更新锁：信号量 + 引用计数。</summary>
    private sealed class UpdateLock
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Refs;
    }

    /// <summary>锁释放句柄：解锁并把引用计数归零的锁项从字典移除。</summary>
    private sealed class UpdateLockReleaser : IDisposable
    {
        private readonly UpdateLock _lock;
        private readonly string _parent;
        private int _disposed;

        public UpdateLockReleaser(UpdateLock l, string parent)
        {
            _lock = l;
            _parent = parent;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lock.Semaphore.Release();
            lock (UpdateLocksGate)
            {
                _lock.Refs--;
                if (_lock.Refs == 0)
                    UpdateLocks.Remove(_parent);
            }
        }
    }

    /// <summary>
    /// 清理被删除路径的索引（由 <see cref="IObjsUpdateHooks"/> 触发）。删除该路径自身
    /// 及其整棵子树的索引行，条件与 <see cref="UpdateDirectoryAsync"/> 相同。
    /// </summary>
    private async Task RemovePathAsync(string path, CancellationToken ct)
    {
        if (!await ShouldAutoUpdateAsync(path, ct)) return;

        var normalized = NormalizeParent(path);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        await db.SearchIndex
            .Where(e => e.Path == normalized || e.Parent == normalized || e.Parent.StartsWith(normalized + "/"))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>增量更新/路径删除的通用前置条件检查。</summary>
    private async Task<bool> ShouldAutoUpdateAsync(string path, CancellationToken ct)
    {
        // 后端必须为 database；auto_update_index 必须开启。
        if (!string.Equals(await GetSettingAsync("search_index", "none"), "database", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(await GetSettingAsync("auto_update_index", "false"), "true", StringComparison.OrdinalIgnoreCase))
            return false;
        // 正在全量构建/更新时跳过，避免与后台任务并发写表。
        lock (_lock)
        {
            if (_progress.State == "running") return false;
            // 索引从未构建过则无从增量，返回 false。
            if (_lastDoneTime is null) return false;
        }
        // 忽略路径内的更新。
        if (await IsIgnorePathAsync(path, ct)) return false;
        return true;
    }

    /// <summary>判断路径是否位于 <c>ignore_paths</c> 设置（按行分隔）的任意忽略前缀下。</summary>
    private async Task<bool> IsIgnorePathAsync(string path, CancellationToken ct)
    {
        var ignorePaths = await GetSettingAsync("ignore_paths", "");
        if (string.IsNullOrWhiteSpace(ignorePaths)) return false;
        var normalized = NormalizeParent(path);
        foreach (var line in ignorePaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0) continue;
            var prefix = NormalizeParent(line);
            if (normalized == prefix || normalized.StartsWith(prefix + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 执行索引遍历（后台任务体）：清空或裁剪既有数据，然后递归写入新条目。
    /// </summary>
    private async Task IndexAsync(IReadOnlyList<string> paths, int maxDepth, bool isUpdate, CancellationToken ct)
    {
        try
        {
            var admin = new User { Id = 0, Username = "indexer", Role = UserRole.Admin };

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();

            // 全量构建先清空整表；增量更新只删除遍历根节点下的旧行再重插。
            if (!isUpdate)
            {
                await db.SearchIndex.ExecuteDeleteAsync(ct);
            }
            else
            {
                foreach (var root in paths)
                {
                    var normalized = NormalizeParent(root);
                    db.SearchIndex.RemoveRange(db.SearchIndex.Where(e => e.Parent == "/" || e.Parent.StartsWith(normalized)));
                }
                await db.SaveChangesAsync(ct);
            }

            foreach (var root in paths)
            {
                ct.ThrowIfCancellationRequested();
                await WalkAsync(root, 0, maxDepth, admin, db, ct);
            }

            lock (_lock)
            {
                _progress.State = "completed";
                _progress.FinishedAt = _time.GetUtcNow();
                _lastDoneTime = _progress.FinishedAt;
                _progress.LastDoneTime = _lastDoneTime;
            }
            await WriteProgressAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _progress.State = "cancelled";
                _progress.FinishedAt = _time.GetUtcNow();
            }
            await WriteProgressAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _progress.State = "failed";
                _progress.FinishedAt = _time.GetUtcNow();
                _progress.Error = ex.Message;
            }
            await WriteProgressAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 递归遍历目录，把条目批量写入索引，并继续下钻子目录（受最大深度限制）。
    /// </summary>
    private async Task WalkAsync(string path, int depth, int maxDepth, User user, OpenListDbContext db, CancellationToken ct)
    {
        var resp = await _fs.ListAsync(new FsListReq { Path = path, PerPage = 1000 }, user, ct);
        if (resp.Code != 200 || resp.Data is null) return;

        lock (_lock) { _progress.Total += resp.Data.Total; }

        var normalizedParent = NormalizeParent(path);
        var batch = new List<SearchIndexEntry>();
        foreach (var entry in resp.Data.Content)
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(new SearchIndexEntry
            {
                Parent = normalizedParent,
                Name = entry.Name,
                IsDir = entry.IsDir,
                Size = entry.Size,
                Path = JoinPath(normalizedParent, entry.Name),
                Type = entry.Type,
            });
            lock (_lock) { _progress.Indexed++; }
        }

        if (batch.Count > 0)
        {
            // 分批 INSERT；SQLite 参数上限较大，但保持每批规模合理。
            foreach (var chunk in Chunk(batch, 200))
            {
                await db.SearchIndex.AddRangeAsync(chunk, ct);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }

        foreach (var entry in resp.Data.Content)
        {
            if (entry.IsDir && (maxDepth < 0 || depth < maxDepth))
            {
                var child = JoinPath(normalizedParent, entry.Name);
                await WalkAsync(child, depth + 1, maxDepth, user, db, ct);
            }
        }
    }

    /// <summary>
    /// 把当前进度快照持久化到 <c>index_progress</c> 设置（JSON 与 Go 的 IndexProgress 同构：
    /// <c>{obj_count, is_done, last_done_time, error}</c>），使进度与“索引已构建”标记跨重启保留。
    /// </summary>
    private async Task WriteProgressAsync(CancellationToken ct)
    {
        try
        {
            IndexProgress snapshot;
            lock (_lock)
            {
                snapshot = new IndexProgress
                {
                    State = _progress.State,
                    Indexed = _progress.Indexed,
                    Total = _progress.Total,
                    StartedAt = _progress.StartedAt,
                    FinishedAt = _progress.FinishedAt,
                    LastDoneTime = _progress.LastDoneTime,
                    Error = _progress.Error,
                };
            }
            var json = JsonSerializer.Serialize(new
            {
                obj_count = snapshot.Indexed,
                is_done = !string.Equals(snapshot.State, "running", StringComparison.Ordinal),
                last_done_time = snapshot.LastDoneTime?.UtcDateTime.ToString("O") ?? "",
                error = snapshot.Error ?? "",
            });
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            await svc.SaveAsync(new List<SettingItem>
            {
                new() { Key = "index_progress", Value = json, Type = SettingValueType.String },
            }, ct);
        }
        catch { /* 持久化失败不影响主流程 */ }
    }

    /// <summary>读取 <c>search_index</c> 设置，读取失败时回退到 <paramref name="fallback"/>。</summary>
    private async Task<string> GetSettingAsync(string key, string fallback)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            var r = await svc.GetAsync(key);
            return (r.Code == 200 && r.Data is not null && !string.IsNullOrWhiteSpace(r.Data.Value))
                ? r.Data.Value
                : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>把路径规整为以 / 开头且无尾部斜杠的标准形式（空视为根目录）。</summary>
    private static string NormalizeParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return path.TrimEnd('/');
    }

    /// <summary>拼接父目录与名称，保证根目录下只输出单个斜杠。</summary>
    private static string JoinPath(string parent, string name)
        => parent == "/" ? "/" + name : parent + "/" + name;

    // 转义用户输入里的 LIKE 通配符，使包含 % 或 _ 的关键字按字面匹配。
    private static string EscapeLike(string input)
        => (input ?? "").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>把列表切分为固定大小的若干批。</summary>
    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}