using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Services;

/// <summary>遍历阶段发现的视频文件（只在本次扫描的内存里存在）。</summary>
/// <param name="Path">VFS 绝对路径。</param>
/// <param name="Size">文件大小（字节）。</param>
/// <param name="Modified">存储端修改时间。</param>
public sealed record MediaFileCandidate(string Path, long Size, DateTimeOffset Modified);

/// <summary>遍历阶段发现的随片元数据文件（NFO / 海报 / 背景图）。</summary>
/// <param name="Path">VFS 绝对路径。</param>
/// <param name="Kind">种类：nfo / poster / backdrop（由 <see cref="MediaNaming"/> 判定）。</param>
public sealed record MediaSidecarCandidate(string Path, string Kind);

/// <summary>一次扫描对数据库造成的变更计数。</summary>
/// <param name="Added">新增条目数。</param>
/// <param name="Updated">新增/更新的文件数。</param>
/// <param name="Removed">移除的文件 + 空条目数。</param>
public readonly record struct ScanChanges(int Added, int Updated, int Removed);

/// <summary>
/// 媒体库扫描执行器：负责"遍历文件系统"和"把结果落库"这两件具体的事，
/// 不含进度登记、并发互斥、取消控制 —— 那些属于 <see cref="IMediaScanService"/> 的职责。
/// <para>
/// 拆出来的收益：扫描编排（单例、有状态）与扫描动作（无状态、可测试）解耦；
/// 后者只依赖 <see cref="IFileSystemService"/>，可以按需替换实现（例如未来直接读存储驱动）。
/// 注册为 transient：它不持有任何作用域资源，只借用调用方传入的 <see cref="OpenListDbContext"/>。
/// </para>
/// <para>
/// NFO 双向化：遍历时顺带收集 <c>.nfo</c> / <c>poster.jpg</c> / <c>fanart.jpg</c> 等
/// sidecar，落库阶段用它们为条目建立初始元数据（<c>MetadataProvider=local</c>，
/// 状态直接 Matched，不再等 TMDB）；之后用户手动刮削时对这类条目只补缺、不覆盖。
/// </para>
/// </summary>
public sealed class MediaLibraryScanner
{
    /// <summary>扫描时使用的内部管理员身份（绕过 BasePath 限制，读全库目录）。</summary>
    private static readonly User ScannerUser = new() { Id = 0, Username = "media-scanner", Role = UserRole.Admin };

    /// <summary>单个 sidecar 的读取上限：NFO 是文本、图片是几千字节级，16MB 绰绰有余且防误读大文件。</summary>
    private const long MaxSidecarBytes = 16 * 1024 * 1024;

    private const int PageSize = 500;

    private readonly IFileSystemService _fs;

    /// <summary>创建扫描执行器。</summary>
    /// <param name="fs">文件系统服务，用于遍历目录与读取 sidecar 内容。</param>
    public MediaLibraryScanner(IFileSystemService fs) => _fs = fs;

    /// <summary>分批落库的默认批次大小：每积累这么多视频文件就写一次库。</summary>
    public const int DefaultBatchSize = 2000;

    /// <summary>
    /// 递归遍历目录，收集视频文件与 sidecar（发现进度直接累加到 <paramref name="progress"/>）。
    /// <para>
    /// 传入 <paramref name="onBatch"/> 时进入**增量模式**：<paramref name="sink"/> 攒满
    /// <paramref name="batchSize"/> 条就整体交给回调写库并清空。这是断点续跑的基础 ——
    /// 遍历中断（取消 / 重启）时，已 flush 的批次都已落库，重跑时
    /// <see cref="MediaScanSession.FlushAsync"/> 按路径判重只会补增量，不会从头再来。
    /// </para>
    /// <para>
    /// 没有 <paramref name="onBatch"/> 时保持旧的一次性收集语义（供
    /// <see cref="PersistAsync"/> 这种"全量在手"的调用路径使用）。
    /// </para>
    /// </summary>
    /// <param name="path">起始目录。</param>
    /// <param name="sink">视频文件收集器（增量模式下会被回调清空）。</param>
    /// <param name="progress">进度对象。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="sidecars">sidecar 收集器；增量模式下**不分批**，由收尾阶段统一处理。</param>
    /// <param name="onBatch">批次回调；为 null 表示一次性收集。</param>
    /// <param name="batchSize">批次大小（仅在传入 <paramref name="onBatch"/> 时生效）。</param>
    public async Task WalkAsync(
        string path, List<MediaFileCandidate> sink, MediaScanProgress progress,
        CancellationToken ct, List<MediaSidecarCandidate>? sidecars = null,
        Func<IReadOnlyList<MediaFileCandidate>, CancellationToken, Task>? onBatch = null,
        int batchSize = DefaultBatchSize)
    {
        ct.ThrowIfCancellationRequested();
        progress.CurrentPath = path;

        var page = 1;
        while (true)
        {
            var resp = await _fs.ListAsync(new FsListReq { Path = path, PerPage = PageSize, Page = page }, ScannerUser, ct);
            if (resp.Code != 200 || resp.Data is null) return;

            foreach (var entry in resp.Data.Content)
            {
                ct.ThrowIfCancellationRequested();
                var childPath = path == "/" ? "/" + entry.Name : path.TrimEnd('/') + "/" + entry.Name;
                if (entry.IsDir)
                {
                    await WalkAsync(childPath, sink, progress, ct, sidecars, onBatch, batchSize);
                    continue;
                }
                if (MediaNaming.IsVideoFile(entry.Name))
                {
                    sink.Add(new MediaFileCandidate(childPath, entry.Size, entry.Modified));
                    progress.Found++;
                    // 攒满一批就落库。异常不吞：写库失败要中止整轮扫描并标记失败，
                    // 否则会留下"看起来在跑、其实什么都没写"的假进度。
                    if (onBatch is not null && sink.Count >= batchSize)
                    {
                        var batch = (IReadOnlyList<MediaFileCandidate>)sink.ToArray();
                        sink.Clear();
                        await onBatch(batch, ct);
                    }
                    continue;
                }
                if (sidecars is null) continue;
                if (MediaNaming.IsNfoFile(entry.Name))
                    sidecars.Add(new MediaSidecarCandidate(childPath, "nfo"));
                else
                {
                    var imageKind = MediaNaming.ClassifyImageFile(entry.Name);
                    if (imageKind is not null) sidecars.Add(new MediaSidecarCandidate(childPath, imageKind));
                }
            }

            if (resp.Data.Content.Count == 0 || page * PageSize >= resp.Data.Total) break;
            page++;
        }
    }

    /// <summary>
    /// 把一整套发现结果一次性写入数据库（等价于「一个批次 + 收尾」）。
    /// <para>
    /// 增量扫描请直接用 <see cref="MediaScanSession"/>（<see cref="WalkAsync"/> 的批次回调
    /// 就是拿它当写入端），本方法保留给"结果全量在手"的调用方与既有测试。
    /// </para>
    /// </summary>
    public async Task<ScanChanges> PersistAsync(
        OpenListDbContext db, long libraryId, string rootPath, MediaLibraryType libraryType,
        IReadOnlyList<MediaFileCandidate> discovered, DateTimeOffset now, CancellationToken ct,
        IReadOnlyList<MediaSidecarCandidate>? sidecars = null)
    {
        var session = await MediaScanSession.CreateAsync(db, _fs, libraryId, rootPath, libraryType, ct);
        await session.FlushAsync(discovered, now, ct);
        return await session.FinalizeAsync(sidecars, now, ct);
    }

    /// <summary>用条目目录里的 NFO 与图片建立初始元数据；目录里两者都没有时不改动条目。</summary>
    internal static async Task ApplySidecarsAsync(
        IFileSystemService fs, OpenListDbContext db, MediaItem item, string rootPath,
        List<string> nfoPaths, List<string> imagePaths, DateTimeOffset now, CancellationToken ct)
    {
        var nfoPath = nfoPaths.Count > 0 ? MediaNaming.SelectNfoPath(item.DirectoryPath, rootPath, nfoPaths) : null;
        MediaNfo? nfo = null;
        if (nfoPath is not null)
        {
            var read = await fs.ReadFileAsync(nfoPath, MaxSidecarBytes, ScannerUser, ct);
            nfo = read.Code == 200 && read.Data is not null
                ? MediaNfoReader.Parse(System.Text.Encoding.UTF8.GetString(read.Data))
                : null;
        }

        // 图片不下载不复制：直接引用存储里的原路径，保持 sidecar 为唯一事实来源。
        var (poster, backdrop) = imagePaths.Count > 0
            ? MediaNaming.SelectImagePaths(item.DirectoryPath, rootPath, imagePaths)
            : (null, null);

        if (nfo is null && poster is null && backdrop is null) return;

        if (poster is not null) item.PosterPath = poster;
        if (backdrop is not null) item.BackdropPath = backdrop;

        if (nfo is not null)
        {
            if (!string.IsNullOrWhiteSpace(nfo.Title)) item.Title = nfo.Title.Trim();
            if (!string.IsNullOrWhiteSpace(nfo.OriginalTitle)) item.OriginalTitle = nfo.OriginalTitle!.Trim();
            if (nfo.Year is not null) item.Year = nfo.Year;
            if (!string.IsNullOrWhiteSpace(nfo.Overview)) item.Overview = nfo.Overview!.Trim();
            if (nfo.Rating is not null) item.Rating = nfo.Rating;
            if (nfo.TmdbId is not null) item.TmdbId = nfo.TmdbId;
            if (nfo.Genres.Count > 0) item.SetGenres(nfo.Genres);

            // 番号域：NFO 里的片商/系列按名取或建后关联到条目。
            if (!string.IsNullOrWhiteSpace(nfo.Studio))
                item.StudioId = await MediaTaxonomySync.ResolveStudioAsync(db, now, nfo.Studio, ct);
            if (!string.IsNullOrWhiteSpace(nfo.Series))
                item.SeriesId = await MediaTaxonomySync.ResolveSeriesAsync(db, now, nfo.Series, ct);

            if (nfo.Persons.Count > 0)
            {
                await MediaPersonSync.ReplaceAsync(db, TimeProvider.System, item.Id,
                    nfo.Persons.Select(p => new MediaPersonSync.Entry(
                        p.Name, null, p.Role, p.Character,
                        // NFO 里的 thumb 是相对条目目录的文件名（或 TMDB 的绝对 URL），
                        // 落库前统一换成可直接读取的路径
                        MediaPersonImage.ToVfsPath(p.ProfilePath, item.DirectoryPath),
                        p.Order)), ct);
            }
        }

        // 有 NFO 但内容为空、或只有图片时仍视为 local：明示"元数据来自本地"，
        // 避免批量刮削对这类条目重复劳动。
        item.MetadataProvider = "local";
        item.Status = MediaItemStatus.Matched;
        item.UpdatedAt = now;
    }
}

/// <summary>
/// 一次扫描的增量落库会话：把「遍历 → 落库」由一次性全量改成可中断的分批推进，
/// 从而支持断点续跑。
/// <para>
/// 生命周期：<see cref="CreateAsync"/> 一次性读出该库现有条目与文件、建立内存索引 →
/// 遍历过程中反复 <see cref="FlushAsync"/> 写入新条目/新文件（**不做删除清理**）→
/// 遍历完整结束后 <see cref="FinalizeAsync"/> 应用 sidecar 并清理已消失的文件与空条目。
/// </para>
/// <para>
/// **为什么清理必须留到最后一步**：增量模式只写入了"已经遍历到"的文件；若每批都执行
/// "删除不在本次结果里的文件"，会把**还没遍历到**的文件整批误删 —— 这是断点续跑相对
/// 旧全量扫描最容易踩的分界点。中断时不做清理是正确的：少删几个已消失文件，
/// 下次完整扫描会把它们清掉；误删现存文件则是不可恢复的数据事故。
/// </para>
/// <para>
/// 会话必须与单个 <see cref="OpenListDbContext"/> 实例配套使用（它依赖 EF 的变更跟踪：
///
/// 新增条目在 <c>SaveChangesAsync</c> 后回填自增主键，后续批次才能写文件外键）。
/// </para>
/// </summary>
public sealed class MediaScanSession
{
    private readonly OpenListDbContext _db;
    private readonly IFileSystemService _fs;
    private readonly long _libraryId;
    private readonly string _rootPath;
    private readonly MediaLibraryType _libraryType;

    /// <summary>库内条目索引（目录路径 → 条目），本轮新增的也实时并入。</summary>
    private readonly Dictionary<string, MediaItem> _itemsByDir;
    /// <summary>库内文件索引（VFS 路径 → 文件记录），本轮新增的也实时并入。</summary>
    private readonly Dictionary<string, MediaFile> _filesByPath;
    /// <summary>扫描开始时就存在的条目 id —— 收尾清理只针对这些，避免把本轮新增的当孤儿删掉。</summary>
    private readonly List<long> _existingItemIds;
    /// <summary>扫描开始时"仍待刮削且无元数据来源"的条目目录（sidecar 要补的对象之一）。</summary>
    private readonly HashSet<string> _pendingAtStart;
    /// <summary>本轮遍历到的全部文件路径（收尾判断"已消失"的基准）。</summary>
    private readonly HashSet<string> _seenPaths = new(StringComparer.Ordinal);
    /// <summary>本轮新建的条目目录。</summary>
    private readonly HashSet<string> _addedDirs = new(StringComparer.Ordinal);

    private int _added;
    private int _updated;

    private MediaScanSession(
        OpenListDbContext db, IFileSystemService fs, long libraryId, string rootPath,
        MediaLibraryType libraryType,
        List<MediaItem> existingItems, List<MediaFile> existingFiles)
    {
        _db = db;
        _fs = fs;
        _libraryId = libraryId;
        _rootPath = rootPath;
        _libraryType = libraryType;
        _itemsByDir = existingItems.ToDictionary(i => i.DirectoryPath, StringComparer.Ordinal);
        _filesByPath = existingFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        _existingItemIds = existingItems.Select(i => i.Id).ToList();
        _pendingAtStart = existingItems
            .Where(i => i.Status == MediaItemStatus.Pending && i.MetadataProvider is null)
            .Select(i => i.DirectoryPath)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>读取该库现有条目与文件，建立会话内存索引。</summary>
    /// <param name="db">本次扫描专用的数据库上下文（全程复用同一个实例）。</param>
    /// <param name="fs">文件系统服务（收尾阶段读 NFO/图片用）。</param>
    /// <param name="libraryId">媒体库 id。</summary>
    /// <param name="rootPath">媒体库根路径。</param>
    /// <param name="libraryType">媒体库类型。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<MediaScanSession> CreateAsync(
        OpenListDbContext db, IFileSystemService fs, long libraryId, string rootPath,
        MediaLibraryType libraryType, CancellationToken ct = default)
    {
        var existingItems = await db.MediaItems.Where(i => i.LibraryId == libraryId).ToListAsync(ct);
        var existingItemIds = existingItems.Select(i => i.Id).ToList();
        var existingFiles = existingItemIds.Count == 0
            ? new List<MediaFile>()
            : await db.MediaFiles.Where(f => existingItemIds.Contains(f.ItemId)).ToListAsync(ct);
        return new MediaScanSession(db, fs, libraryId, rootPath, libraryType, existingItems, existingFiles);
    }

    /// <summary>
    /// 写入一个批次：建立缺失条目并写文件记录。**不执行任何删除**（见类型注释）。
    /// 返回本轮累计的 (新增条目数, 新增/更新文件数)，便于调用方实时刷新进度。
    /// </summary>
    /// <param name="batch">本批遍历到的视频文件。</param>
    /// <param name="now">批次时间戳。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<(int Added, int Updated)> FlushAsync(
        IReadOnlyList<MediaFileCandidate> batch, DateTimeOffset now, CancellationToken ct = default)
    {
        if (batch.Count == 0) return (_added, _updated);

        var groups = batch
            .GroupBy(f => MediaNaming.ResolveItemDirectory(f.Path, _rootPath), StringComparer.Ordinal)
            .ToList();

        // 第一段：建立缺失条目并保存，让新增条目拿到自增主键（下一段写文件外键要用）。
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (_itemsByDir.ContainsKey(group.Key)) continue;

            var (title, year) = MediaNaming.ParseTitle(group.Key, _rootPath);
            var item = new MediaItem
            {
                LibraryId = _libraryId,
                Type = MediaNaming.ResolveItemType(_libraryType, _rootPath, group.Key),
                Title = title,
                Year = year,
                // 番号域：目录名/文件名里能解析出番号就直接落库（不影响状态与 provider）。
                Code = MediaNaming.ExtractCode(group.Key),
                Status = MediaItemStatus.Pending,
                DirectoryPath = group.Key,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.MediaItems.Add(item);
            _itemsByDir[group.Key] = item;
            _addedDirs.Add(group.Key);
            _added++;
        }
        await _db.SaveChangesAsync(ct);

        // 第二段：写文件记录；顺带为存量条目补番号（老库扫描先于番号功能时 Code 为空，这里幂等回填）。
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (!_itemsByDir.TryGetValue(group.Key, out var item)) continue;
            if (item.Code is null) item.Code = MediaNaming.ExtractCode(group.Key);

            foreach (var file in group)
            {
                _seenPaths.Add(file.Path);
                if (_filesByPath.TryGetValue(file.Path, out var existing))
                {
                    if (existing.Size != file.Size)
                    {
                        existing.Size = file.Size;
                        _updated++;
                    }
                    continue;
                }
                var record = new MediaFile
                {
                    ItemId = item.Id,
                    Path = file.Path,
                    Size = file.Size,
                    CreatedAt = now,
                };
                _db.MediaFiles.Add(record);
                _filesByPath[file.Path] = record;
                _updated++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return (_added, _updated);
    }

    /// <summary>
    /// 收尾：应用 sidecar 元数据，清理源目录中已消失的文件与失去全部文件的空条目。
    /// 只在**完整遍历结束后**调用；中断时跳过它（见类型注释）。
    /// </summary>
    /// <param name="sidecars">遍历期间收集到的 sidecar（NFO / 海报 / 背景图）。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ScanChanges> FinalizeAsync(
        IReadOnlyList<MediaSidecarCandidate>? sidecars, DateTimeOffset now, CancellationToken ct = default)
    {
        // NFO 双向化：对新增条目、以及仍处于待刮削且无元数据来源的存量条目，
        // 用目录里的 NFO/图片建立初始元数据。local 条目状态直接 Matched，
        // 批量 TMDB 刮削（只挑 Pending）会自动跳过它们。
        if (sidecars is { Count: > 0 })
        {
            var nfoPaths = sidecars.Where(s => s.Kind == "nfo").Select(s => s.Path).ToList();
            var imagePaths = sidecars.Where(s => s.Kind != "nfo").Select(s => s.Path).ToList();

            foreach (var dir in _addedDirs.Concat(_pendingAtStart))
            {
                ct.ThrowIfCancellationRequested();
                if (!_itemsByDir.TryGetValue(dir, out var item)) continue;
                await MediaLibraryScanner.ApplySidecarsAsync(_fs, _db, item, _rootPath, nfoPaths, imagePaths, now, ct);
            }
            await _db.SaveChangesAsync(ct);
        }

        // 清理：源目录中已消失的文件 + 失去全部文件的空条目（连同个人状态与演职员关联）。
        var removed = 0;
        var staleFiles = await _db.MediaFiles.Where(f => _existingItemIds.Contains(f.ItemId)).ToListAsync(ct);
        foreach (var stale in staleFiles.Where(f => !_seenPaths.Contains(f.Path)))
        {
            _db.MediaFiles.Remove(stale);
            _filesByPath.Remove(stale.Path);
            removed++;
        }
        await _db.SaveChangesAsync(ct);

        var remainingItemIds = await _db.MediaFiles
            .Where(f => _existingItemIds.Contains(f.ItemId))
            .Select(f => f.ItemId)
            .Distinct()
            .ToListAsync(ct);
        var orphans = _itemsByDir.Values
            .Where(i => _existingItemIds.Contains(i.Id) && !remainingItemIds.Contains(i.Id))
            .ToList();
        if (orphans.Count > 0)
        {
            var orphanIds = orphans.Select(o => o.Id).ToList();
            _db.UserMediaStates.RemoveRange(await _db.UserMediaStates.Where(s => orphanIds.Contains(s.ItemId)).ToListAsync(ct));
            _db.MediaItemPersons.RemoveRange(await _db.MediaItemPersons.Where(r => orphanIds.Contains(r.ItemId)).ToListAsync(ct));
            _db.MediaItems.RemoveRange(orphans);
            foreach (var orphan in orphans) _itemsByDir.Remove(orphan.DirectoryPath);
            removed += orphans.Count;
        }
        await _db.SaveChangesAsync(ct);
        return new ScanChanges(_added, _updated, removed);
    }
}
