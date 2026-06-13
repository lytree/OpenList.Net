namespace OpenList.Core.Interfaces;

public interface IScanService
{
    Task StartScanAsync(string path, double limit = 0, CancellationToken ct = default);
    Task StopScanAsync(CancellationToken ct = default);
    bool IsRunning { get; }
    long ScannedCount { get; }
}
