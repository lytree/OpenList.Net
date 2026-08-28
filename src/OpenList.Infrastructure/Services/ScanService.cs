using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 手动扫描服务：触发对存储的文件系统重新扫描。
/// 扫描进度保存在内存中。对应 Go 参考实现 <c>server/handles/scan.go</c>。
/// </summary>
public sealed class ScanService : IScanService
{
    private readonly IFileSystemService _fs;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private ScanProgress _progress = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 创建扫描服务实例。
    /// </summary>
    /// <param name="fs">文件系统服务，用于读取目录。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public ScanService(IFileSystemService fs, TimeProvider? time = null)
    {
        _fs = fs;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 启动对 <paramref name="path"/> 的扫描。若已有扫描在运行则返回冲突。
    /// </summary>
    /// <param name="path">要扫描的起始目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启动成功返回成功响应；已有扫描在运行返回 409。</returns>
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

        // 后台一次性扫描任务（fire-and-forget）。
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

    /// <summary>
    /// 停止正在进行的扫描。
    /// </summary>
    public Task<Resp> StopAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 获取当前扫描进度的快照副本。
    /// </summary>
    public Task<Resp<ScanProgress>> GetProgressAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            // 返回快照副本，避免外部改动进度状态。
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

    /// <summary>
    /// 递归列出目录并累计统计（文件数与总数由下层 <see cref="IFileSystemService"/> 提供）。
    /// </summary>
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