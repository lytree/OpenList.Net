using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 手动扫描服务：触发对存储目录的重新扫描。
/// 与 OpenList Go 的 <c>server/handles/scan.go</c> 对应。
/// </summary>
public interface IScanService
{
    /// <summary>从指定路径开始扫描。</summary>
    Task<Resp> StartAsync(string path, CancellationToken ct = default);
    /// <summary>停止扫描。</summary>
    Task<Resp> StopAsync(CancellationToken ct = default);
    /// <summary>获取扫描进度。</summary>
    Task<Resp<ScanProgress>> GetProgressAsync(CancellationToken ct = default);
}

/// <summary>
/// 扫描进度信息。
/// </summary>
public sealed class ScanProgress
{
    /// <summary>扫描状态，默认 "idle"。</summary>
    public string State { get; set; } = "idle";
    /// <summary>当前正在扫描的路径，可为空。</summary>
    public string? CurrentPath { get; set; }
    /// <summary>已扫描的条目数。</summary>
    public long Scanned { get; set; }
    /// <summary>待扫描的条目总数。</summary>
    public long Total { get; set; }
    /// <summary>开始时间。</summary>
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>结束时间。</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}
