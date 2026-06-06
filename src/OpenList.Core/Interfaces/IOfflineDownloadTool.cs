namespace OpenList.Core.Interfaces;

/// <summary>Status of an offline download task</summary>
public class DownloadStatus
{
    public long TotalBytes { get; set; }
    public double Progress { get; set; }
    public string? NewGID { get; set; }
    public bool Completed { get; set; }
    public string? StatusText { get; set; }
    public string? Error { get; set; }
}

/// <summary>Arguments for adding a URL to download</summary>
public class AddUrlArgs
{
    public string Url { get; set; } = "";
    public string TempDir { get; set; } = "";
    public string TaskId { get; set; } = "";
}

/// <summary>Interface for offline download tools (aria2, qBittorrent, etc.)</summary>
public interface IOfflineDownloadTool
{
    string Name { get; }
    Task<string?> InitAsync(CancellationToken ct = default);
    bool IsReady { get; }
    Task<string> AddUrlAsync(AddUrlArgs args, CancellationToken ct = default);
    Task RemoveAsync(string gid, CancellationToken ct = default);
    Task<DownloadStatus> GetStatusAsync(string gid, CancellationToken ct = default);
}
