using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 媒体库扫描服务：递归遍历库路径，识别视频文件并按"视频所在目录 = 条目"归组，
/// 生成/更新 <see cref="MediaItem"/> 与 <see cref="MediaFile"/>，清理已消失的记录。
/// 扫描在后台运行，进度按库记录在内存中，历史落库到 x_media_scan_logs。
/// </summary>
public sealed partial class MediaScanService : IMediaScanService
{
    /// <summary>识别为视频的扩展名集合。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts",
        ".m2ts", ".mpg", ".mpeg", ".rmvb", ".rm", ".3gp", ".vob", ".iso", ".strm",
    };

    /// <summary>扫描时使用的内部管理员身份（绕过 BasePath 限制，读全库目录）。</summary>
    private static readonly User ScannerUser = new() { Id = 0, Username = "media-scanner", Role = UserRole.Admin };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileSystemService _fs;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<long, MediaScanProgress> _progress = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private readonly object _gate = new();

    /// <summary>创建媒体库扫描服务实例。</summary>
    /// <param name="scopeFactory">作用域工厂：后台任务需要独立创建 DbContext。</param>
    /// <param name="fs">文件系统服务，用于遍历目录。</param>
    /// <param name="time">时间源（可选，默认系统时钟）。</param>
    public MediaScanService(IServiceScopeFactory scopeFactory, IFileSystemService fs, TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _fs = fs;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp> StartAsync(long libraryId, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp.Fail("permission denied", 403);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        var lib = await db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        if (lib is null) return Resp.Fail("library not found", RespCodes.NotFound);

        lock (_gate)
        {
            if (_running.ContainsKey(libraryId)) return Resp.Fail("scan already running", RespCodes.Conflict);
            var cts = new CancellationTokenSource();
            _running[libraryId] = cts;
            _progress[libraryId] = new MediaScanProgress { State = "running", StartedAt = _time.GetUtcNow() };
            _ = Task.Run(() => RunAsync(libraryId, lib.Path, lib.Type, cts.Token), CancellationToken.None);
        }
        return Resp.Ok();
    }

    /// <inheritdoc />
    public Task<Resp> StopAsync(long libraryId, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Task.FromResult(Resp.Fail("permission denied", 403));
        if (_running.TryGetValue(libraryId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(Resp.Ok());
        }
        return Task.FromResult(Resp.Fail("scan not running", RespCodes.BadRequest));
    }

    /// <inheritdoc />
    public Task<Resp<MediaScanProgress>> GetProgressAsync(long libraryId, CancellationToken ct = default)
    {
        var snapshot = _progress.TryGetValue(libraryId, out var p)
            ? new MediaScanProgress
            {
                State = p.State,
                CurrentPath = p.CurrentPath,
                Found = p.Found,
                Added = p.Added,
                Updated = p.Updated,
                Removed = p.Removed,
                StartedAt = p.StartedAt,
                FinishedAt = p.FinishedAt,
            }
            : new MediaScanProgress();
        return Task.FromResult(Resp<MediaScanProgress>.Ok(snapshot));
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaScanLog>>> ListLogsAsync(long libraryId, int limit, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp<List<MediaScanLog>>.Fail("permission denied", 403);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        var logs = await db.MediaScanLogs.AsNoTracking()
            .Where(l => l.LibraryId == libraryId)
            .OrderByDescending(l => l.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);
        return Resp<List<MediaScanLog>>.Ok(logs);
    }

    /// <summary>扫描主流程（后台）。</summary>
    private async Task RunAsync(long libraryId, string rootPath, MediaLibraryType libraryType, CancellationToken ct)
    {
        var progress = _progress[libraryId];
        var discovered = new List<DiscoveredFile>();
        var log = new MediaScanLog { LibraryId = libraryId, StartedAt = _time.GetUtcNow() };

        try
        {
            await WalkAsync(rootPath, discovered, progress, ct);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            var (added, updated, removed) = await PersistAsync(db, libraryId, rootPath, libraryType, discovered, ct);

            log.Added = added;
            log.Updated = updated;
            log.Removed = removed;
            log.FinishedAt = _time.GetUtcNow();
            db.MediaScanLogs.Add(log);

            var lib = await db.MediaLibraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct);
            if (lib is not null)
            {
                lib.LastScanAt = log.FinishedAt;
                lib.UpdatedAt = log.FinishedAt.Value;
            }
            await db.SaveChangesAsync(ct);

            progress.Added = added;
            progress.Updated = updated;
            progress.Removed = removed;
            progress.State = "completed";
            progress.FinishedAt = log.FinishedAt;
        }
        catch (OperationCanceledException)
        {
            progress.State = "cancelled";
            progress.FinishedAt = _time.GetUtcNow();
            log.Error = "cancelled";
            log.FinishedAt = progress.FinishedAt;
            await TryWriteLogAsync(log);
        }
        catch (Exception ex)
        {
            progress.State = "failed";
            progress.FinishedAt = _time.GetUtcNow();
            log.Error = ex.Message;
            log.FinishedAt = progress.FinishedAt;
            await TryWriteLogAsync(log);
        }
        finally
        {
            if (_running.TryRemove(libraryId, out var cts)) cts.Dispose();
        }
    }

    /// <summary>失败/取消时尽力写入扫描历史（独立作用域，避免影响主流程异常）。</summary>
    private async Task TryWriteLogAsync(MediaScanLog log)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            db.MediaScanLogs.Add(log);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // 历史写入失败不影响扫描结果状态。
        }
    }

    /// <summary>递归遍历目录，收集视频文件。</summary>
    private async Task WalkAsync(string path, List<DiscoveredFile> sink, MediaScanProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress.CurrentPath = path;

        var page = 1;
        while (true)
        {
            var resp = await _fs.ListAsync(new FsListReq { Path = path, PerPage = 500, Page = page }, ScannerUser, ct);
            if (resp.Code != 200 || resp.Data is null) return;

            foreach (var entry in resp.Data.Content)
            {
                ct.ThrowIfCancellationRequested();
                var childPath = path == "/" ? "/" + entry.Name : path.TrimEnd('/') + "/" + entry.Name;
                if (entry.IsDir)
                {
                    await WalkAsync(childPath, sink, progress, ct);
                    continue;
                }
                var ext = Path.GetExtension(entry.Name);
                if (!VideoExtensions.Contains(ext)) continue;

                sink.Add(new DiscoveredFile(childPath, entry.Size, entry.Modified));
                progress.Found++;
            }

            if (resp.Data.Content.Count == 0 || page * 500 >= resp.Data.Total) break;
            page++;
        }
    }

    /// <summary>把发现结果写入数据库：新增/更新文件与条目、清理消失的记录。</summary>
    private async Task<(int Added, int Updated, int Removed)> PersistAsync(
        OpenListDbContext db, long libraryId, string rootPath, MediaLibraryType libraryType,
        List<DiscoveredFile> discovered, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var existingItems = await db.MediaItems.Where(i => i.LibraryId == libraryId).ToListAsync(ct);
        var existingItemIds = existingItems.Select(i => i.Id).ToList();
        var existingFiles = existingItemIds.Count == 0
            ? []
            : await db.MediaFiles.Where(f => existingItemIds.Contains(f.ItemId)).ToListAsync(ct);
        var itemsByDir = existingItems.ToDictionary(i => i.DirectoryPath, StringComparer.Ordinal);
        var filesByPath = existingFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);

        var groups = discovered
            .GroupBy(f => ResolveItemDirectory(f.Path, rootPath), StringComparer.Ordinal)
            .ToList();

        // 第一步：建立/更新条目并保存，让新增条目拿到自增主键。
        var added = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (itemsByDir.ContainsKey(group.Key)) continue;

            var (title, year) = ParseTitle(group.Key, rootPath);
            var item = new MediaItem
            {
                LibraryId = libraryId,
                Type = ResolveItemType(libraryType, rootPath, group.Key),
                Title = title,
                Year = year,
                Status = MediaItemStatus.Pending,
                DirectoryPath = group.Key,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.MediaItems.Add(item);
            itemsByDir[group.Key] = item;
            added++;
        }
        await db.SaveChangesAsync(ct);

        // 第二步：写入文件记录（新增条目的 Id 已回填）。
        var updated = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (!itemsByDir.TryGetValue(group.Key, out var item)) continue;

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

        // 第三步：清理源目录中已消失的文件，以及失去全部文件的空条目。
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
        return (added, updated, removed);
    }

    /// <summary>把视频文件映射到条目目录：视频所在目录；库根下的散文件以文件路径本身为条目键。</summary>
    private static string ResolveItemDirectory(string filePath, string rootPath)
    {
        var slash = filePath.LastIndexOf('/');
        if (slash <= 0) return filePath;
        var parent = filePath[..slash];
        // 视频直接位于库根（或更深层目录结构不明确时），用文件本身作为条目，避免把整个库当一个条目。
        if (parent == rootPath || parent == "/") return filePath;
        return parent;
    }

    /// <summary>Mixed 库按目录层级判定类型：条目目录含"Season/第x季"特征视为剧集。</summary>
    private static MediaLibraryType ResolveItemType(MediaLibraryType libraryType, string rootPath, string itemDir)
    {
        if (libraryType != MediaLibraryType.Mixed) return libraryType;
        return SeasonRegex().IsMatch(itemDir) ? MediaLibraryType.TvShow : MediaLibraryType.Movie;
    }

    /// <summary>从条目目录或文件名解析标题与年份。</summary>
    private static (string Title, int? Year) ParseTitle(string itemDir, string rootPath)
    {
        var raw = itemDir;
        if (raw == rootPath || raw == "/")
            raw = raw.TrimEnd('/');
        var name = raw[(raw.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(name)) name = "Unknown";

        // 若条目键是文件路径，去掉扩展名。
        var ext = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext)) name = name[..^ext.Length];

        var yearMatch = YearRegex().Match(name);
        int? year = yearMatch.Success && int.TryParse(yearMatch.Value, out var y) ? y : null;

        var title = name;
        if (yearMatch.Success) title = name[..yearMatch.Index];
        title = title.Replace('.', ' ').Replace('_', ' ');
        title = BracketRegex().Replace(title, " ");
        title = MultiSpaceRegex().Replace(title, " ").Trim(' ', '-', '[', ']', '【', '】');
        if (string.IsNullOrWhiteSpace(title)) title = name;
        return (title, year);
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"[\[\(【（][^\]\)】）]*[\]\)】）]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"(?i)(season\s*\d+|第\s*[0-9一二三四五六七八九十]+\s*季|s\d{1,2})")]
    private static partial Regex SeasonRegex();

    /// <summary>遍历时收集到的视频文件（仅用于本次扫描内存排序）。</summary>
    private sealed record DiscoveredFile(string Path, long Size, DateTimeOffset Modified);
}
