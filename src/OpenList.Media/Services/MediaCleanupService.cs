using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;
using OpenList.Vfs;

namespace OpenList.Media.Services;

/// <summary>
/// 媒体库数据清理：孤儿条目、重复文件、单条删除与回收站。
/// <para>
/// **所有写操作都幂等**：id 不存在、库不可见、文件早已消失，一律跳过而不是报错 ——
/// 清理页天然是「预览 → 停一会 → 执行」，中间很可能有一次扫描把行顺手清掉了，
/// 这时候回 404 只会让用户看到一个莫名其妙的失败。真正的失败（文件删不掉、
/// 磁盘只读）才返回错误，并且**保留对应的数据库行**，避免出现「文件还在、记录没了」。
/// </para>
/// <para>
/// 文件操作统一走 <see cref="IFileSystemService"/> + 内部系统身份（<see cref="User.System"/>）：
/// 清理是后台语义，不该被当前请求用户的 BasePath 或密码目录规则挡住。
/// 是否碰文件由调用方决定：孤儿只删行；重复 / 单条删除才按所属库的
/// <see cref="MediaLibrary.TrashDirectory"/> 决定「移进回收站」还是「直接删」。
/// </para>
/// </summary>
public sealed class MediaCleanupService : IMediaCleanupService
{
    /// <summary>文件操作使用的内部身份（<see cref="User.IsAdmin"/> 对负数 id 为真，可绕过路径过滤）。</summary>
    private static readonly User CleanerUser = User.System("media-cleanup");

    /// <summary>重复确认只比对前 1 MB：同尺寸 + 同首段 + 同时长已足够，也不必把整部片子读一遍。</summary>
    private const int HashPrefixBytes = 1024 * 1024;

    /// <summary>一次请求最多确认多少组候选（按体积倒序取）。防止几十万文件时把请求拖住。</summary>
    private const int MaxDuplicateGroups = 200;

    /// <summary>一次请求最多算多少个文件的哈希。</summary>
    private const int MaxHashedFiles = 2000;

    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;
    private readonly MountService _mount;
    private readonly IFileSystemService _fs;
    private readonly ILogger<MediaCleanupService> _logger;

    /// <summary>创建清理服务。</summary>
    /// <param name="db">EF Core 上下文。</param>
    /// <param name="libraries">库服务，只借用它的可见性判定。</param>
    /// <param name="mount">挂载根，用于存在性判断与哈希前缀读取。</param>
    /// <param name="fs">文件系统服务，用于删除 / 移动 / 建目录。</param>
    /// <param name="logger">可选日志。</param>
    public MediaCleanupService(
        OpenListDbContext db,
        IMediaLibraryService libraries,
        MountService mount,
        IFileSystemService fs,
        ILogger<MediaCleanupService>? logger = null)
    {
        _db = db;
        _libraries = libraries;
        _mount = mount;
        _fs = fs;
        _logger = logger ?? NullLogger<MediaCleanupService>.Instance;
    }

    // ------------------------------------------------------------------
    // 孤儿条目
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<Resp<List<OrphanItem>>> ListOrphansAsync(long libraryId, User user, CancellationToken ct = default)
    {
        var (libs, error) = await ResolveLibrariesAsync(libraryId, user, ct);
        if (error != RespCodes.Ok) return Resp<List<OrphanItem>>.Fail("library not found", error);
        if (libs.Count == 0) return Resp<List<OrphanItem>>.Ok([]);

        var libIds = libs.Select(l => l.Id).ToList();
        var libNames = libs.ToDictionary(l => l.Id, l => l.Name);
        var items = await _db.MediaItems.AsNoTracking()
            .Where(i => libIds.Contains(i.LibraryId))
            .Select(i => new { i.Id, i.Title, i.DirectoryPath, i.LibraryId })
            .ToListAsync(ct);
        if (items.Count == 0) return Resp<List<OrphanItem>>.Ok([]);

        var itemIds = items.Select(i => i.Id).ToList();
        var files = await _db.MediaFiles.AsNoTracking()
            .Where(f => itemIds.Contains(f.ItemId))
            .Select(f => new { f.ItemId, f.Path })
            .ToListAsync(ct);
        var filesByItem = files.GroupBy(f => f.ItemId).ToDictionary(g => g.Key, g => g.Select(x => x.Path).ToList());

        var orphans = new List<OrphanItem>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var paths = filesByItem.TryGetValue(item.Id, out var list) ? list : [];
            // 短路：只要还有一个文件在，这条就不是孤儿。逐项 stat 全部文件在云驱动上太贵。
            if (paths.Count > 0 && paths.Any(FileExists)) continue;

            orphans.Add(new OrphanItem
            {
                ItemId = item.Id,
                Title = item.Title,
                DirectoryPath = item.DirectoryPath,
                LibraryId = item.LibraryId,
                LibraryName = libNames.GetValueOrDefault(item.LibraryId, string.Empty),
                MissingFiles = paths.Count,
            });
        }

        var ordered = orphans
            .OrderBy(o => o.LibraryId)
            .ThenBy(o => o.Title, StringComparer.CurrentCulture)
            .ToList();
        return Resp<List<OrphanItem>>.Ok(ordered);
    }

    /// <inheritdoc />
    public Task<Resp> DeleteOrphansAsync(IReadOnlyList<long> itemIds, User user, CancellationToken ct = default)
        => DeleteItemsCoreAsync(itemIds, deleteFiles: false, user, ct);

    // ------------------------------------------------------------------
    // 重复文件
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<Resp<List<DuplicateGroup>>> ListDuplicatesAsync(long libraryId, User user, CancellationToken ct = default)
    {
        var (libs, error) = await ResolveLibrariesAsync(libraryId, user, ct);
        if (error != RespCodes.Ok) return Resp<List<DuplicateGroup>>.Fail("library not found", error);
        if (libs.Count == 0) return Resp<List<DuplicateGroup>>.Ok([]);

        var libIds = libs.Select(l => l.Id).ToList();
        // 初筛在内存里做：SQLite 对 (Size, DurationSeconds) 的分组能算，但接下来要按组读文件算哈希，
        // 中间无论如何要落一份候选清单。文件行本身只取必要字段，避免把整张表拉成实体。
        var rows = await (
            from f in _db.MediaFiles.AsNoTracking()
            join i in _db.MediaItems.AsNoTracking() on f.ItemId equals i.Id
            where libIds.Contains(i.LibraryId) && f.Size > 0
            select new CandidateRow
            {
                ItemId = i.Id,
                LibraryId = i.LibraryId,
                Title = i.Title,
                DirectoryPath = i.DirectoryPath,
                FilePath = f.Path,
                Size = f.Size,
                DurationSeconds = f.DurationSeconds,
                Resolution = f.Resolution,
            }).ToListAsync(ct);
        if (rows.Count < 2) return Resp<List<DuplicateGroup>>.Ok([]);

        var hashed = 0;
        var groups = new List<DuplicateGroup>();
        var candidates = rows
            .GroupBy(r => (r.Size, r.DurationSeconds))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Key.Size)
            .Take(MaxDuplicateGroups);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (hashed >= MaxHashedFiles) break;

            var byHash = new Dictionary<string, List<CandidateRow>>(StringComparer.Ordinal);
            foreach (var row in candidate)
            {
                if (hashed >= MaxHashedFiles) break;
                hashed++;
                var hash = TryHashPrefix(row.FilePath);
                if (hash is null) continue;
                if (!byHash.TryGetValue(hash, out var bucket)) byHash[hash] = bucket = [];
                bucket.Add(row);
            }

            foreach (var (_, bucket) in byHash)
            {
                if (bucket.Count < 2) continue;
                var items = bucket
                    .GroupBy(r => r.ItemId)
                    .Select(g =>
                    {
                        var first = g.First();
                        return new DuplicateItem
                        {
                            ItemId = first.ItemId,
                            Title = first.Title,
                            DirectoryPath = first.DirectoryPath,
                            LibraryId = first.LibraryId,
                            FilePath = first.FilePath,
                            DurationSeconds = first.DurationSeconds,
                            Resolution = first.Resolution,
                        };
                    })
                    .OrderBy(i => i.Title, StringComparer.CurrentCulture)
                    .ToList();

                groups.Add(new DuplicateGroup
                {
                    SizeBytes = bucket[0].Size,
                    Count = bucket.Count,
                    Items = items,
                });
            }
        }

        var ordered = groups
            .OrderByDescending(g => g.SizeBytes)
            .ThenBy(g => g.Items.Count > 0 ? g.Items[0].Title : string.Empty, StringComparer.CurrentCulture)
            .ToList();
        return Resp<List<DuplicateGroup>>.Ok(ordered);
    }

    /// <inheritdoc />
    public Task<Resp> DeleteDuplicatesAsync(IReadOnlyList<long> itemIds, User user, CancellationToken ct = default)
        => DeleteItemsCoreAsync(itemIds, deleteFiles: true, user, ct);

    // ------------------------------------------------------------------
    // 单条删除 / 回收站
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<Resp> DeleteItemAsync(long itemId, bool deleteFiles, User user, CancellationToken ct = default)
        => DeleteItemsCoreAsync([itemId], deleteFiles, user, ct);

    /// <inheritdoc />
    public async Task<Resp> EmptyTrashAsync(long libraryId, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp.Fail("permission denied", RespCodes.Forbidden);

        var lib = await _db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        if (lib is null || !_libraries.CanSeeLibrary(user, lib))
            return Resp.Fail("library not found", RespCodes.NotFound);

        if (string.IsNullOrWhiteSpace(lib.TrashDirectory))
            return Resp.Fail("trash directory not configured", RespCodes.BadRequest);

        var trash = MediaLibraryService.NormalizePath(lib.TrashDirectory);
        // 防御：回收站指向根目录（或某个挂载点根）时，一次点击会清空整个存储。
        if (trash == "/")
            return Resp.Fail("trash directory must not be the storage root", RespCodes.BadRequest);

        var upath = new UPath(trash);
        if (!_mount.Root.DirectoryExists(upath)) return Resp.Ok(); // 已经空了，幂等

        var children = _mount.Root.EnumeratePaths(upath).Select(p => p.ToString()).ToList();
        if (children.Count == 0) return Resp.Ok();

        var resp = await _fs.RemoveAsync(children, CleanerUser, ct);
        return resp.Code == RespCodes.Ok
            ? Resp.Ok()
            : Resp.Fail($"trash cleanup failed: {resp.Message}", resp.Code);
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析「要扫描/操作的库集合」：<paramref name="libraryId"/> 为 0 表示全部可见库；
    /// 指定了不存在或不可见的库时返回 404（与媒体门面同口径，不泄露私有库存在性）。
    /// </summary>
    private async Task<(List<MediaLibrary> Libraries, int ErrorCode)> ResolveLibrariesAsync(
        long libraryId, User user, CancellationToken ct)
    {
        if (libraryId > 0)
        {
            var lib = await _db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == libraryId, ct);
            if (lib is null || !_libraries.CanSeeLibrary(user, lib))
                return ([], RespCodes.NotFound);
            return ([lib], RespCodes.Ok);
        }

        var all = await _db.MediaLibraries.AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct);
        return (all.Where(l => _libraries.CanSeeLibrary(user, l)).ToList(), RespCodes.Ok);
    }

    /// <summary>
    /// 批量删除条目（数据库行 + 可选文件）。这是三条删除入口的共同实现：
    /// 孤儿（不碰文件）、重复（碰文件）、单条（由参数决定）。
    /// </summary>
    private async Task<Resp> DeleteItemsCoreAsync(
        IReadOnlyList<long> itemIds, bool deleteFiles, User user, CancellationToken ct)
    {
        if (!user.IsAdmin()) return Resp.Fail("permission denied", RespCodes.Forbidden);

        // 幂等入口：去重、丢弃非法 id，空集合直接成功（预览页可能一个都没勾）。
        var ids = itemIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return Resp.Ok();

        var libs = await _db.MediaLibraries.AsNoTracking().ToListAsync(ct);
        var byLib = libs.ToDictionary(l => l.Id);
        var items = await _db.MediaItems.Where(i => ids.Contains(i.Id)).ToListAsync(ct);

        var doomed = new List<MediaItem>();
        foreach (var item in items)
        {
            // 不可见库里的条目当作不存在：直接跳过。
            if (byLib.TryGetValue(item.LibraryId, out var lib) && !_libraries.CanSeeLibrary(user, lib)) continue;
            doomed.Add(item);
        }
        if (doomed.Count == 0) return Resp.Ok();

        var failures = new List<string>();
        if (deleteFiles)
        {
            var kept = new List<MediaItem>();
            foreach (var item in doomed)
            {
                ct.ThrowIfCancellationRequested();
                byLib.TryGetValue(item.LibraryId, out var lib);
                var error = await DeleteItemFilesAsync(item, lib, ct);
                if (error is null) continue;
                // 文件没处理干净就不删行：否则磁盘上留下孤儿文件，下次扫描又把它捞回来。
                kept.Add(item);
                failures.Add($"{item.Title}: {error}");
            }
            if (kept.Count > 0)
                doomed = doomed.Where(i => !kept.Contains(i)).ToList();
        }

        if (doomed.Count > 0)
        {
            var delIds = doomed.Select(i => i.Id).ToList();

            _db.MediaFiles.RemoveRange(await _db.MediaFiles.Where(f => delIds.Contains(f.ItemId)).ToListAsync(ct));
            _db.MediaItemPersons.RemoveRange(await _db.MediaItemPersons.Where(r => delIds.Contains(r.ItemId)).ToListAsync(ct));
            _db.UserMediaStates.RemoveRange(await _db.UserMediaStates.Where(s => delIds.Contains(s.ItemId)).ToListAsync(ct));
            _db.MediaWatchSessions.RemoveRange(await _db.MediaWatchSessions.Where(s => delIds.Contains(s.ItemId)).ToListAsync(ct));
            _db.MediaPlaylistItems.RemoveRange(await _db.MediaPlaylistItems.Where(p => delIds.Contains(p.ItemId)).ToListAsync(ct));

            // 想看 / 下载记录保留（它们记的是「曾经想要」这件事），只把指向已删条目的回填写空，
            // 否则想看页会显示一条「已入库」但点进去 404 的记录。
            foreach (var want in await _db.MediaWants
                         .Where(w => w.ItemId != null && delIds.Contains(w.ItemId.Value)).ToListAsync(ct))
                want.ItemId = null;
            foreach (var download in await _db.MediaDownloads
                         .Where(d => d.ItemId != null && delIds.Contains(d.ItemId.Value)).ToListAsync(ct))
                download.ItemId = null;

            _db.MediaItems.RemoveRange(doomed);
            await _db.SaveChangesAsync(ct);
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning("media cleanup: {Failed} item(s) kept because file ops failed: {First}",
                failures.Count, failures[0]);
            return Resp.Fail(
                $"deleted {doomed.Count} item(s), {failures.Count} kept because their files could not be removed: {failures[0]}",
                RespCodes.InternalError);
        }
        return Resp.Ok();
    }

    /// <summary>
    /// 处理一个条目下的文件：配了回收站就移进去，否则直接删。
    /// 返回 null 表示成功；返回字符串表示失败原因（调用方据此保留数据库行）。
    /// </summary>
    private async Task<string?> DeleteItemFilesAsync(MediaItem item, MediaLibrary? lib, CancellationToken ct)
    {
        var paths = await _db.MediaFiles.AsNoTracking()
            .Where(f => f.ItemId == item.Id)
            .Select(f => f.Path)
            .ToListAsync(ct);
        var live = paths.Where(FileExists).ToList();
        if (live.Count == 0) return null; // 文件早没了：只剩删行

        var trash = lib?.TrashDirectory;
        if (string.IsNullOrWhiteSpace(trash))
        {
            var resp = await _fs.RemoveAsync(live, CleanerUser, ct);
            return resp.Code == RespCodes.Ok ? null : resp.Message;
        }

        var trashRoot = MediaLibraryService.NormalizePath(trash);
        foreach (var path in live)
        {
            ct.ThrowIfCancellationRequested();
            var source = MediaLibraryService.NormalizePath(path);
            // 文件本来就在回收站里（重复执行、或库的回收站指向自身子树）：直接删，别再套一层。
            if (IsUnder(source, trashRoot))
            {
                var direct = await _fs.RemoveAsync([source], CleanerUser, ct);
                if (direct.Code != RespCodes.Ok) return direct.Message;
                continue;
            }

            // 目标镜像源文件的完整路径：/trash + /media/movies/x/a.mkv。
            // 这样不会因为两个库各有同名条目而互相覆盖，回收站里也能看出原位置。
            var dest = UniqueTrashPath(trashRoot + source);
            var dir = dest[..dest.LastIndexOf('/')];
            var mkdirError = await EnsureDirectoryAsync(dir, ct);
            if (mkdirError is not null) return mkdirError;

            var moved = await _fs.RenameAsync(source, dest, CleanerUser, ct);
            if (moved.Code != RespCodes.Ok) return moved.Message;
        }
        return null;
    }

    /// <summary>文件在 VFS 上是否存在。查询失败按「存在」处理：宁可漏报孤儿，也不误删记录。</summary>
    private bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return _mount.Root.FileExists(new UPath(MediaLibraryService.NormalizePath(path)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "media cleanup: stat {Path} failed, treating it as existing", path);
            return true;
        }
    }

    /// <summary>逐级创建目录（<c>MkdirAsync</c> 只建一级，回收站的镜像路径可能有很多级）。</summary>
    private async Task<string?> EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        var norm = MediaLibraryService.NormalizePath(path);
        if (norm == "/") return null;

        var current = string.Empty;
        foreach (var part in norm.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (_mount.Root.DirectoryExists(new UPath(current))) continue;
            var resp = await _fs.MkdirAsync(current, CleanerUser, ct);
            if (resp.Code != RespCodes.Ok && !_mount.Root.DirectoryExists(new UPath(current)))
                return resp.Message;
        }
        return null;
    }

    /// <summary>回收站里已有同名文件时补 <c>(2)</c>、<c>(3)</c>…，避免覆盖上一次删掉的同名文件。</summary>
    private string UniqueTrashPath(string path)
    {
        if (!Occupied(path)) return path;

        var slash = path.LastIndexOf('/');
        var dir = slash > 0 ? path[..slash] : string.Empty;
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : string.Empty;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{dir}/{stem} ({i}){ext}";
            if (!Occupied(candidate)) return candidate;
        }
        return $"{dir}/{stem}-{Guid.NewGuid():N}{ext}";
    }

    private bool Occupied(string path)
    {
        try
        {
            var upath = new UPath(path);
            return _mount.Root.FileExists(upath) || _mount.Root.DirectoryExists(upath);
        }
        catch { return false; }
    }

    /// <summary>算文件前 1 MB 的 SHA-256；读不到（文件刚被删、驱动报错）返回 null，该文件不参与判定。</summary>
    private string? TryHashPrefix(string path)
    {
        try
        {
            var upath = new UPath(MediaLibraryService.NormalizePath(path));
            using var stream = _mount.Root.OpenFile(upath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[HashPrefixBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "media cleanup: hash {Path} failed, skipped", path);
            return null;
        }
    }

    /// <summary>路径是否等于 <paramref name="basePath"/> 或位于其下（与库路径判定同一口径）。</summary>
    private static bool IsUnder(string path, string basePath)
        => basePath == "/"
           || string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 重复判定的候选行。用可变类而不是匿名类型：EF 投影到 <c>new { }</c> 后
    /// 还要在内存里分组、排序、二次哈希，具名类型读起来更清楚。
    /// </summary>
    private sealed class CandidateRow
    {
        public long ItemId { get; init; }
        public long LibraryId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string DirectoryPath { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public long Size { get; init; }
        public int? DurationSeconds { get; init; }
        public string? Resolution { get; init; }
    }
}
