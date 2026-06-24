using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Manual scan service: triggers directory rescans for storages.
/// Tracks progress in-memory. Mirrors OpenList Go <c>server/handles/scan.go</c>.
/// </summary>
public sealed class ScanService : IScanService
{
    private readonly IFileSystemService _fs;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private ScanProgress _progress = new();
    private CancellationTokenSource? _cts;

    public ScanService(IFileSystemService fs, TimeProvider? time = null)
    {
        _fs = fs;
        _time = time ?? TimeProvider.System;
    }

    public Task<Resp> StartAsync(string path, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_progress.State == "running")
                return Task.FromResult(Resp.Fail("scan already running", RespCodes.Conflict));

            _cts = new CancellationTokenSource();
            _progress = new ScanProgress
            {
                State = "running",
                StartedAt = _time.GetUtcNow(),
                Scanned = 0,
                Total = 0,
            };
        }

        // Fire-and-forget scan task
        _ = Task.Run(async () =>
        {
            try
            {
                await ScanDirectoryAsync(path, _cts.Token);
                lock (_lock)
                {
                    _progress.State = "completed";
                    _progress.FinishedAt = _time.GetUtcNow();
                }
            }
            catch (OperationCanceledException)
            {
                lock (_lock)
                {
                    _progress.State = "cancelled";
                    _progress.FinishedAt = _time.GetUtcNow();
                }
            }
            catch (Exception)
            {
                lock (_lock)
                {
                    _progress.State = "failed";
                    _progress.FinishedAt = _time.GetUtcNow();
                }
            }
        }, CancellationToken.None);

        return Task.FromResult(Resp.Ok());
    }

    public Task<Resp> StopAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }
        return Task.FromResult(Resp.Ok());
    }

    public Task<Resp<ScanProgress>> GetProgressAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Return a snapshot copy to avoid external mutation.
            var snapshot = new ScanProgress
            {
                State = _progress.State,
                CurrentPath = _progress.CurrentPath,
                Scanned = _progress.Scanned,
                Total = _progress.Total,
                StartedAt = _progress.StartedAt,
                FinishedAt = _progress.FinishedAt,
            };
            return Task.FromResult(Resp<ScanProgress>.Ok(snapshot));
        }
    }

    private async Task ScanDirectoryAsync(string path, CancellationToken ct)
    {
        var resp = await _fs.ListAsync(new FsListReq { Path = path, PerPage = 1000 },
            new User { Id = 0, Username = "scanner", Role = UserRole.Admin }, ct);
        if (resp.Code != 200 || resp.Data is null) return;

        lock (_lock)
        {
            _progress.Total += resp.Data.Total;
        }

        foreach (var entry in resp.Data.Content)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDir)
            {
                var childPath = string.IsNullOrEmpty(path) || path == "/"
                    ? "/" + entry.Name
                    : path.TrimEnd('/') + "/" + entry.Name;
                await ScanDirectoryAsync(childPath, ct);
            }
            lock (_lock)
            {
                _progress.Scanned++;
                _progress.CurrentPath = path;
            }
        }
    }
}
