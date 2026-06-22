using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Manual scan service: triggers directory rescans for storages.
/// Mirrors OpenList Go <c>server/handles/scan.go</c>.
/// </summary>
public interface IScanService
{
    Task<Resp> StartAsync(string path, CancellationToken ct = default);
    Task<Resp> StopAsync(CancellationToken ct = default);
    Task<Resp<ScanProgress>> GetProgressAsync(CancellationToken ct = default);
}

public sealed class ScanProgress
{
    public string State { get; set; } = "idle";
    public string? CurrentPath { get; set; }
    public long Scanned { get; set; }
    public long Total { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
