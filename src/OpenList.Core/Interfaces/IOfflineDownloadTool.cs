using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Offline download tool abstraction (aria2, qBittorrent, 115, pikpak, thunder, etc.).
/// Mirrors OpenList Go <c>pkg/task/offline_download</c>.
/// </summary>
public interface IOfflineDownloadTool
{
    string Name { get; }
    bool Enabled { get; }
    Task<Resp<string>> AddDownloadAsync(string url, string destPath, CancellationToken ct = default);
    Task<Resp> CancelAsync(string taskId, CancellationToken ct = default);
    Task<Resp<OfflineDownloadStatus>> GetStatusAsync(string taskId, CancellationToken ct = default);
}

public sealed class OfflineDownloadStatus
{
    public string State { get; set; } = "pending";
    public long Downloaded { get; set; }
    public long Total { get; set; }
    public string? Error { get; set; }
}
