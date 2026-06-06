using System.Threading;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

public class ScanService : IScanService
{
    private readonly IFileSystemService _fs;
    private readonly IStorageService _storageService;
    private CancellationTokenSource? _cts;
    private long _scannedCount;
    private volatile bool _isRunning;

    public ScanService(IFileSystemService fs, IStorageService storageService)
    {
        _fs = fs;
        _storageService = storageService;
    }

    public bool IsRunning => _isRunning;
    public long ScannedCount => Interlocked.Read(ref _scannedCount);

    public async Task StartScanAsync(string path, double limit = 0, CancellationToken ct = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("manual scan is running, please try later");

        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _scannedCount, 0);
        _isRunning = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await RecursivelyListAsync(path, _cts.Token);
            }
            finally
            {
                _isRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }, ct);
    }

    public Task StopScanAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            throw new InvalidOperationException("manual scan is not running");
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RecursivelyListAsync(string path, CancellationToken ct)
    {
        try
        {
            var objs = await _fs.ListAsync(path, new ListArgs { Refresh = true }, ct);
            Interlocked.Add(ref _scannedCount, objs.Count);

            foreach (var obj in objs.Where(o => o.IsDir()))
            {
                if (ct.IsCancellationRequested) return;
                await RecursivelyListAsync(obj.GetPath(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* log error */ }
    }
}
