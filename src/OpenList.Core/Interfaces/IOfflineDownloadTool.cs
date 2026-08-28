using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 离线下载工具抽象（aria2、qBittorrent、115、pikpak、thunder 等）。
/// 与 OpenList Go 的 <c>pkg/task/offline_download</c> 对应。
/// </summary>
public interface IOfflineDownloadTool
{
    /// <summary>工具名称。</summary>
    string Name { get; }
    /// <summary>是否启用。</summary>
    bool Enabled { get; }
    /// <summary>添加一个下载任务并返回任务 id。</summary>
    Task<Resp<string>> AddDownloadAsync(string url, string destPath, CancellationToken ct = default);
    /// <summary>取消一个下载任务。</summary>
    Task<Resp> CancelAsync(string taskId, CancellationToken ct = default);
    /// <summary>获取下载任务的状态。</summary>
    Task<Resp<OfflineDownloadStatus>> GetStatusAsync(string taskId, CancellationToken ct = default);
}

/// <summary>
/// 离线下载任务的状态信息。
/// </summary>
public sealed class OfflineDownloadStatus
{
    /// <summary>状态，默认 "pending"。</summary>
    public string State { get; set; } = "pending";
    /// <summary>已下载字节数。</summary>
    public long Downloaded { get; set; }
    /// <summary>总字节数。</summary>
    public long Total { get; set; }
    /// <summary>错误信息，可为空。</summary>
    public string? Error { get; set; }
}
