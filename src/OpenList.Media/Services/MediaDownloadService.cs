using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Services;

/// <summary>
/// 想看作品下载的实现（见 <see cref="IMediaDownloadService"/>）。
/// <para>
/// 落盘位置 = 媒体库根路径 + 清洗后的作品名（带年份）。只建**一级**目录：库根路径一定存在，
/// 多级创建得靠 VFS 逐层 Mkdir，收益不如让用户把库指到更深的目录。
/// </para>
/// <para>
/// 「下载完成」与「已入库」是两件事：离线下载先下到本机临时目录，再另起一个转移任务上传进
/// 库目录，而 <see cref="IOfflineDownloadService"/> 只把**下载段**的任务 id 给我们。因此这里
/// 看到 Succeeded 只代表「下完了」，入库靠 <see cref="IMediaWantService.LinkIngestedAsync"/>
/// 在扫描之后按 TmdbId 对表确认。
/// </para>
/// </summary>
public sealed class MediaDownloadService : IMediaDownloadService
{
    /// <summary>下载完成但还没入库时，重试扫描的最小间隔（转移任务可能刚好还没跑完）。</summary>
    private static readonly TimeSpan ScanRetryInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 「下载完成但还没入库」的重试窗口。超过之后不再自动扫库：
    /// 作品名对不上、用户下错了资源、或者产物被手动删掉，都会永远等不到入库，
    /// 不设上限就是一辈子每两分钟全库扫一次。窗口外仍可让用户手动「扫描」。
    /// </summary>
    private static readonly TimeSpan ScanRetryWindow = TimeSpan.FromHours(24);

    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;
    private readonly IMediaWantService _wants;
    private readonly IOfflineDownloadService _offline;
    private readonly ITaskService _tasks;
    private readonly IFileSystemService _fs;
    private readonly IMediaScanService _scans;
    private readonly TimeProvider _time;
    private readonly ILogger<MediaDownloadService>? _logger;

    /// <summary>创建下载服务实例。</summary>
    public MediaDownloadService(
        OpenListDbContext db,
        IMediaLibraryService libraries,
        IMediaWantService wants,
        IOfflineDownloadService offline,
        ITaskService tasks,
        IFileSystemService fs,
        IMediaScanService scans,
        TimeProvider? time = null,
        ILogger<MediaDownloadService>? logger = null)
    {
        _db = db;
        _libraries = libraries;
        _wants = wants;
        _offline = offline;
        _tasks = tasks;
        _fs = fs;
        _scans = scans;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Resp<MediaDownload>> SubmitAsync(
        SubmitMediaDownloadReq req, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp<MediaDownload>.Fail("permission denied", RespCodes.Forbidden);

        var want = await _db.MediaWants.FirstOrDefaultAsync(w => w.Id == req.WantId, ct);
        if (want is null) return Resp<MediaDownload>.Fail("want not found", RespCodes.NotFound);
        if (want.ItemId is not null)
            return Resp<MediaDownload>.Fail("already in library", RespCodes.BadRequest);

        var library = await _db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == req.LibraryId, ct);
        if (library is null || !_libraries.CanSeeLibrary(user, library))
            return Resp<MediaDownload>.Fail("library not found", RespCodes.NotFound);

        var url = (req.Url ?? string.Empty).Trim();
        if (!IsSupportedUrl(url))
            return Resp<MediaDownload>.Fail("url must be a magnet link or an http(s) url", RespCodes.BadRequest);

        var directory = BuildTargetDirectory(library.Path, want);
        var mk = await _fs.MkdirAsync(directory, user, ct);
        // Conflict = 目录已经在（上一次下载留下的），对它接着下正是想要的行为。
        if (mk.Code != RespCodes.Ok && mk.Code != RespCodes.Conflict)
            return Resp<MediaDownload>.Fail($"failed to create target directory: {mk.Message}", mk.Code);

        var added = await _offline.AddAsync(new OfflineDownloadReq
        {
            Urls = [url],
            Path = directory,
            Tool = req.Tool,
            // 下载完成后删临时文件：媒体库要的是产物，不是工具的工作目录。
            DeletePolicy = OfflineDownloadService.DeleteOnUploadSucceed,
        }, user, ct);
        if (added.Code != RespCodes.Ok || added.Data is null)
            return Resp<MediaDownload>.Fail(added.Message, added.Code);

        var now = _time.GetUtcNow();
        var row = new MediaDownload
        {
            WantId = want.Id,
            LibraryId = library.Id,
            Tool = (req.Tool ?? string.Empty).Trim(),
            TaskId = added.Data.Tasks.FirstOrDefault(),
            Title = want.Title,
            Url = url,
            TargetDirectory = directory,
            Status = MediaDownloadStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MediaDownloads.Add(row);
        await _db.SaveChangesAsync(ct);
        return Resp<MediaDownload>.Ok(row);
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaDownload>>> ListAsync(
        long? wantId, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp<List<MediaDownload>>.Fail("permission denied", RespCodes.Forbidden);

        var q = _db.MediaDownloads.AsNoTracking();
        if (wantId is not null) q = q.Where(d => d.WantId == wantId.Value);

        // Id 倒序 = 最近提交的在前（不用 CreatedAt：SQLite 不能在 SQL 层排 DateTimeOffset）。
        var rows = await q.OrderByDescending(d => d.Id).Take(200).ToListAsync(ct);
        return Resp<List<MediaDownload>>.Ok(rows);
    }

    /// <inheritdoc />
    public async Task<Resp> CancelAsync(long id, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp.Fail("permission denied", RespCodes.Forbidden);

        var row = await _db.MediaDownloads.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return Resp.Fail("download not found", RespCodes.NotFound);
        if (IsTerminal(row.Status)) return Resp.Ok();

        if (!string.IsNullOrEmpty(row.TaskId))
        {
            var cancel = await _tasks.CancelAsync(ResolveDownType(row.Tool), row.TaskId, ct);
            // 任务已被清空/重启丢失时 cancel 会 404：这不算失败，行照旧落到「已取消」。
            if (cancel.Code != RespCodes.Ok && cancel.Code != RespCodes.NotFound)
                return Resp.Fail(cancel.Message, cancel.Code);
        }

        row.Status = MediaDownloadStatus.Cancelled;
        row.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <inheritdoc />
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        var active = await _db.MediaDownloads
            .Where(d => d.Status == MediaDownloadStatus.Pending || d.Status == MediaDownloadStatus.Downloading)
            .OrderBy(d => d.Id)
            .ToListAsync(ct);

        var now = _time.GetUtcNow();
        var changed = 0;

        foreach (var row in active)
        {
            if (string.IsNullOrEmpty(row.TaskId))
            {
                // 下载器没接住（AddAsync 返回空列表）——过一会儿仍为空的直接判失败。
                if (now - row.CreatedAt > TimeSpan.FromMinutes(10))
                {
                    row.Status = MediaDownloadStatus.Failed;
                    row.Error = "downloader did not accept the task";
                    row.UpdatedAt = now;
                    changed++;
                }
                continue;
            }

            var info = await _tasks.InfoAsync(ResolveDownType(row.Tool), row.TaskId, ct);
            if (info.Code != RespCodes.Ok || info.Data is null)
            {
                // 任务被用户在任务中心清掉了：行不能永远停在「下载中」。
                row.Status = MediaDownloadStatus.Failed;
                row.Error = "download task not found";
                row.UpdatedAt = now;
                changed++;
                continue;
            }

            var task = info.Data;
            if (task.Total > 0)
                row.Progress = Math.Clamp(task.Progress * 100.0 / task.Total, 0, 100);

            switch (task.State)
            {
                case TaskState.Pending:
                    break;
                case TaskState.Running:
                    if (row.Status != MediaDownloadStatus.Downloading)
                    {
                        row.Status = MediaDownloadStatus.Downloading;
                        changed++;
                    }
                    row.UpdatedAt = now;
                    break;
                case TaskState.Succeeded:
                    row.Status = MediaDownloadStatus.Completed;
                    row.Progress = 100;
                    row.CompletedAt ??= task.EndTime ?? now;
                    row.UpdatedAt = now;
                    changed++;
                    break;
                case TaskState.Errored:
                    row.Status = MediaDownloadStatus.Failed;
                    row.Error = task.Error ?? "download failed";
                    row.UpdatedAt = now;
                    changed++;
                    break;
                case TaskState.Canceling:
                case TaskState.Canceled:
                    row.Status = MediaDownloadStatus.Cancelled;
                    row.UpdatedAt = now;
                    changed++;
                    break;
            }
        }

        if (changed > 0 || active.Count > 0) await _db.SaveChangesAsync(ct);

        // 下载完成后扫一次库：产物刚由转移任务写进库目录，不扫就永远不入库。
        await ScanCompletedAsync(now, ct);

        // 已入库回填与扫描无强依赖：用户也可能自己把文件放进库再扫，这里一并补齐。
        await _wants.LinkIngestedAsync(null, ct);
        return changed;
    }

    /// <summary>对「下载完成但还没入库」的行触发库扫描（同一库每轮只触发一次，2 分钟内不重复）。</summary>
    private async Task ScanCompletedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var pending = await _db.MediaDownloads
            .Where(d => d.Status == MediaDownloadStatus.Completed && d.IngestedAt == null)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var system = User.System("media-download");
        var started = new HashSet<long>();
        var touched = false;
        foreach (var row in pending)
        {
            if (now - row.UpdatedAt < ScanRetryInterval) continue;
            // 超过窗口说明「扫了也进不来」，停手；否则这里的每行都会周期性触发全库扫描。
            if (row.CompletedAt is { } done && now - done > ScanRetryWindow) continue;
            row.UpdatedAt = now;
            touched = true;
            if (!started.Add(row.LibraryId)) continue;

            var resp = await _scans.StartAsync(row.LibraryId, system, ct);
            // 409 = 已经在扫，下一轮再来，不需要记错误。
            if (resp.Code == RespCodes.Ok)
                _logger?.LogInformation("media download {DownloadId} finished, scan of library {LibraryId} started", row.Id, row.LibraryId);
        }
        if (touched) await _db.SaveChangesAsync(ct);
    }

    /// <summary>解析下载器对应的「下载段」任务类型（与 <see cref="IOfflineDownloadService"/> 的映射一致）。</summary>
    internal static TaskType ResolveDownType(string? tool) => (tool ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "aria2" => TaskType.Aria2Down,
        "qbit" or "qbittorrent" => TaskType.QbitDown,
        "transmission" => TaskType.TransmissionDown,
        _ => TaskType.OfflineDownload,
    };

    /// <summary>终态：失败与取消不再轮询。</summary>
    private static bool IsTerminal(MediaDownloadStatus status)
        => status is MediaDownloadStatus.Failed or MediaDownloadStatus.Cancelled;

    /// <summary>磁力链接或 http(s) 绝对地址；其它（ftp/ed2k/file://）一律拒绝。</summary>
    private static bool IsSupportedUrl(string url)
    {
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>库根路径 + 作品名（带年份）。年份能区分同名重拍片，也让扫描的命名规则更好命中。</summary>
    private static string BuildTargetDirectory(string libraryPath, MediaWant want)
    {
        var name = MediaExternal.SanitizeDirectoryName(want.Title);
        if (want.Year is > 0) name = $"{name} ({want.Year})";
        var root = (libraryPath ?? string.Empty).TrimEnd('/');
        return root.Length == 0 ? "/" + name : root + "/" + name;
    }
}
