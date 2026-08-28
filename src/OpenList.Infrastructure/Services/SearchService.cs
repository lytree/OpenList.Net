using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
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
    private readonly object _lock = new();
    private IndexProgress _progress = new();
    private CancellationTokenSource? _cts;
    private DateTimeOffset? _lastDoneTime;

    /// <summary>
    /// 创建搜索索引服务实例。
    /// </summary>
    /// <param name="fs">文件系统服务，用于遍历条目与实时搜索。</param>
    /// <param name="scopes">作用域工厂，用于按需创建数据库上下文。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public SearchService(IFileSystemService fs, IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _fs = fs;
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
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
        return Resp.Ok();
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
    /// </summary>
    public async Task<Resp<SearchResp>> SearchAsync(SearchReq req, CancellationToken ct = default)
    {
        // 根据 search_index 设置决定后端。
        var backend = await GetSettingAsync("search_index", "none");
        if (!string.Equals(backend, "database", StringComparison.OrdinalIgnoreCase))
        {
            // none / bleve（未实现）→ 实时文件系统搜索。
            return await _fs.SearchAsync(req, new User { Id = 0, Username = "search", Role = UserRole.Admin }, ct);
        }

        return await SearchDatabaseAsync(req, ct);
    }

    /// <summary>
    /// 在持久化索引中搜索：按名称 LIKE 过滤，支持递归/非递归范围与分页。
    /// </summary>
    private async Task<Resp<SearchResp>> SearchDatabaseAsync(SearchReq req, CancellationToken ct)
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

        var total = await q.CountAsync(ct);
        var page = Math.Max(1, req.Page);
        var perPage = req.PerPage < 1 ? int.MaxValue : req.PerPage;
        var rows = await q.OrderByDescending(e => e.Id)
            .Skip((page - 1) * perPage).Take(perPage)
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

        return Resp<SearchResp>.Ok(new SearchResp { Content = rows, Total = total });
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
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _progress.State = "cancelled";
                _progress.FinishedAt = _time.GetUtcNow();
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _progress.State = "failed";
                _progress.FinishedAt = _time.GetUtcNow();
                _progress.Error = ex.Message;
            }
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