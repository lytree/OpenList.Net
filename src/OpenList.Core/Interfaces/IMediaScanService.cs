using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 媒体库扫描服务：遍历库路径发现视频文件，建立/更新媒体条目与文件记录。
/// 扫描作为后台任务运行（fire-and-forget），进度可查，历史落库到 x_media_scan_logs。
/// </summary>
public interface IMediaScanService
{
    /// <summary>启动对指定媒体库的扫描（需要媒体库管理权限）。已有同库扫描在运行时返回 409。</summary>
    Task<Resp> StartAsync(long libraryId, User user, CancellationToken ct = default);
    /// <summary>停止指定媒体库的扫描。</summary>
    Task<Resp> StopAsync(long libraryId, User user, CancellationToken ct = default);
    /// <summary>获取指定媒体库的扫描进度快照。</summary>
    Task<Resp<MediaScanProgress>> GetProgressAsync(long libraryId, CancellationToken ct = default);
    /// <summary>列出指定媒体库的扫描历史（最近 N 条）。</summary>
    Task<Resp<List<MediaScanLog>>> ListLogsAsync(long libraryId, int limit, User user, CancellationToken ct = default);
}

/// <summary>媒体库扫描进度快照。</summary>
public sealed class MediaScanProgress
{
    /// <summary>扫描状态（idle / running / completed / failed / cancelled）。</summary>
    public string State { get; set; } = "idle";
    /// <summary>当前正在处理的路径。</summary>
    public string? CurrentPath { get; set; }
    /// <summary>已发现的视频文件数。</summary>
    public long Found { get; set; }
    /// <summary>新增条目数。</summary>
    public int Added { get; set; }
    /// <summary>更新条目数。</summary>
    public int Updated { get; set; }
    /// <summary>移除条目数。</summary>
    public int Removed { get; set; }
    /// <summary>开始时间。</summary>
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>结束时间。</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}
