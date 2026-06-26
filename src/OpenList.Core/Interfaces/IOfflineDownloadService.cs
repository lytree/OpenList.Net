using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Orchestrates offline downloads through aria2 / qBittorrent / Transmission.
/// Validates the destination path, picks the requested tool, creates a tracking
/// task via <see cref="ITaskService"/>, and polls the tool until the download
/// completes (or fails / is cancelled). Mirrors OpenList Go
/// <c>pkg/task/offline_download</c>.
/// </summary>
public interface IOfflineDownloadService
{
    /// <summary>
    /// Add URLs to the requested offline-download tool and start tracking them.
    /// Returns the new task id(s).
    /// </summary>
    Task<Resp<OfflineDownloadResp>> AddAsync(OfflineDownloadReq req, User user, CancellationToken ct = default);
}

public sealed class OfflineDownloadResp
{
    /// <summary>Created task ids (one per URL).</summary>
    public List<string> Tasks { get; set; } = [];
}
