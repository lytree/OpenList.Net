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

    /// <summary>递归遍历目录，收集视频文件与 sidecar（发现进度直接累加到 <paramref name="progress"/>）。</summary>
    public async Task WalkAsync(
        string path, List<MediaFileCandidate> sink, MediaScanProgress progress,
        CancellationToken ct, List<MediaSidecarCandidate>? sidecars = null)
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
                    await WalkAsync(childPath, sink, progress, ct, sidecars);
                    continue;
                }
                if (MediaNaming.IsVideoFile(entry.Name))
                {
                    sink.Add(new MediaFileCandidate(childPath, entry.Size, entry.Modified));
                    progress.Found++;
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
    /// 把发现结果写入数据库：建立/更新条目与文件记录，清理已消失的文件与空条目。
    /// 分三步保存是为了让新增条目先拿到自增主键，再写文件外键。
    /// sidecar 元数据在第一步与第二步之间应用（此时新增条目已有主键）。
    /// </summary>
    public async Task<ScanChanges> PersistAsync(
        OpenListDbContext db, long libraryId, string rootPath, MediaLibraryType libraryType,
        IReadOnlyList<MediaFileCandidate> discovered, DateTimeOffset now, CancellationToken ct,
        IReadOnlyList<MediaSidecarCandidate>? sidecars = null)
    {
        var existingItems = await db.MediaItems.Where(i => i.LibraryId == libraryId).ToListAsync(ct);
        var existingItemIds = existingItems.Select(i => i.Id).ToList();
        var existingFiles = existingItemIds.Count == 0
            ? []
            : await db.MediaFiles.Where(f => existingItemIds.Contains(f.ItemId)).ToListAsync(ct);
        var itemsByDir = existingItems.ToDictionary(i => i.DirectoryPath, StringComparer.Ordinal);
        var filesByPath = existingFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);

        var groups = discovered
            .GroupBy(f => MediaNaming.ResolveItemDirectory(f.Path, rootPath), StringComparer.Ordinal)
            .ToList();

        // 第一步：建立/更新条目并保存，让新增条目拿到自增主键。
        var added = 0;
        var addedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (itemsByDir.ContainsKey(group.Key)) continue;

            var (title, year) = MediaNaming.ParseTitle(group.Key, rootPath);
            var item = new MediaItem
            {
                LibraryId = libraryId,
                Type = MediaNaming.ResolveItemType(libraryType, rootPath, group.Key),
                Title = title,
                Year = year,
                // 番号域：目录名/文件名里能解析出番号就直接落库（不影响状态与 provider）。
                Code = MediaNaming.ExtractCode(group.Key),
                Status = MediaItemStatus.Pending,
                DirectoryPath = group.Key,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.MediaItems.Add(item);
            itemsByDir[group.Key] = item;
            addedDirs.Add(group.Key);
            added++;
        }
        await db.SaveChangesAsync(ct);

        // 第二步（NFO 双向化）：对新增条目、以及仍处于待刮削且无元数据来源的存量条目，
        // 用目录里的 NFO/图片建立初始元数据。local 条目状态直接 Matched，
        // 批量 TMDB 刮削（只挑 Pending）会自动跳过它们。
        if (sidecars is { Count: > 0 })
        {
            var nfoPaths = sidecars.Where(s => s.Kind == "nfo").Select(s => s.Path).ToList();
            var imagePaths = sidecars.Where(s => s.Kind != "nfo").Select(s => s.Path).ToList();
            var pendingExisting = existingItems
                .Where(i => !addedDirs.Contains(i.DirectoryPath)
                            && i.Status == MediaItemStatus.Pending
                            && i.MetadataProvider is null)
                .Select(i => i.DirectoryPath)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                if (!addedDirs.Contains(group.Key) && !pendingExisting.Contains(group.Key)) continue;
                if (!itemsByDir.TryGetValue(group.Key, out var item)) continue;
                await ApplySidecarsAsync(db, item, rootPath, nfoPaths, imagePaths, now, ct);
            }
            await db.SaveChangesAsync(ct);
        }

        // 第三步：写入文件记录（新增条目的 Id 已回填）。
        // 顺带为存量条目补番号（老库扫描先于番号功能时 Code 为空，这里幂等回填）。
        var updated = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (!itemsByDir.TryGetValue(group.Key, out var item)) continue;
            if (item.Code is null) item.Code = MediaNaming.ExtractCode(group.Key);

            foreach (var file in group)
            {
                if (filesByPath.TryGetValue(file.Path, out var existing))
                {
                    if (existing.Size != file.Size)
                    {
                        existing.Size = file.Size;
                        updated++;
                    }
                    continue;
                }
                db.MediaFiles.Add(new MediaFile
                {
                    ItemId = item.Id,
                    Path = file.Path,
                    Size = file.Size,
                    CreatedAt = now,
                });
                updated++;
            }
        }
        await db.SaveChangesAsync(ct);

        // 第四步：清理源目录中已消失的文件，以及失去全部文件的空条目（连同个人状态与演职员关联）。
        var removed = 0;
        var seenPaths = discovered.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var staleFiles = await db.MediaFiles.Where(f => existingItemIds.Contains(f.ItemId)).ToListAsync(ct);
        foreach (var stale in staleFiles.Where(f => !seenPaths.Contains(f.Path)))
        {
            db.MediaFiles.Remove(stale);
            removed++;
        }
        await db.SaveChangesAsync(ct);

        var remainingItemIds = await db.MediaFiles
            .Where(f => existingItemIds.Contains(f.ItemId))
            .Select(f => f.ItemId)
            .Distinct()
            .ToListAsync(ct);
        var orphans = existingItems.Where(i => !remainingItemIds.Contains(i.Id)).ToList();
        if (orphans.Count > 0)
        {
            var orphanIds = orphans.Select(o => o.Id).ToList();
            db.UserMediaStates.RemoveRange(await db.UserMediaStates.Where(s => orphanIds.Contains(s.ItemId)).ToListAsync(ct));
            db.MediaItemPersons.RemoveRange(await db.MediaItemPersons.Where(r => orphanIds.Contains(r.ItemId)).ToListAsync(ct));
            db.MediaItems.RemoveRange(orphans);
            removed += orphans.Count;
        }
        await db.SaveChangesAsync(ct);
        return new ScanChanges(added, updated, removed);
    }

    /// <summary>用条目目录里的 NFO 与图片建立初始元数据；目录里两者都没有时不改动条目。</summary>
    private async Task ApplySidecarsAsync(
        OpenListDbContext db, MediaItem item, string rootPath,
        List<string> nfoPaths, List<string> imagePaths, DateTimeOffset now, CancellationToken ct)
    {
        var nfoPath = nfoPaths.Count > 0 ? MediaNaming.SelectNfoPath(item.DirectoryPath, rootPath, nfoPaths) : null;
        MediaNfo? nfo = null;
        if (nfoPath is not null)
        {
            var read = await _fs.ReadFileAsync(nfoPath, MaxSidecarBytes, ScannerUser, ct);
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
                        p.Name, null, p.Role, p.Character, null, p.Order)), ct);
            }
        }

        // 有 NFO 但内容为空、或只有图片时仍视为 local：明示"元数据来自本地"，
        // 避免批量刮削对这类条目重复劳动。
        item.MetadataProvider = "local";
        item.Status = MediaItemStatus.Matched;
        item.UpdatedAt = now;
    }
}
