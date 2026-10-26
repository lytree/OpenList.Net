using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

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
    /// <summary>
    /// 列出当前用户可见库的扫描任务（跨库一次取回，供常驻任务卡使用）。
    /// 正在运行的取内存进度，空闲的退回最近一次扫描历史。
    /// </summary>
    Task<Resp<List<MediaScanTask>>> ListTasksAsync(User user, CancellationToken ct = default);
}

/// <summary>跨库扫描任务项（任务卡数据源）。</summary>
public sealed class MediaScanTask
{
    /// <summary>媒体库 id。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
    /// <summary>媒体库名称。</summary>
    [JsonPropertyName("library_name")]
    public string LibraryName { get; set; } = string.Empty;
    /// <summary>媒体库根路径。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>任务状态（idle / running / completed / failed / cancelled）。</summary>
    public string State { get; set; } = "idle";
    /// <summary>当前正在处理的路径。</summary>
    [JsonPropertyName("current_path")]
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
    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>结束时间。</summary>
    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>最近一次扫描的错误信息，可为空。</summary>
    public string? Error { get; set; }
    /// <summary>是否正在运行（前端据此显示"停止"而不是"扫描"）。</summary>
    [JsonPropertyName("is_running")]
    public bool IsRunning { get; set; }
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
