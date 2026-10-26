using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Services;

/// <summary>
/// 媒体库扫描编排服务：并发互斥、进度登记、取消、扫描历史与任务卡。
/// 真正的"遍历 + 落库"交给 <see cref="MediaLibraryScanner"/>，
/// 命名规则交给 <see cref="MediaNaming"/>。
/// <para>
/// 注册为单例：进度/运行状态必须是进程级共享的。它不直接持有 <see cref="OpenListDbContext"/>
/// （scoped），需要时自己 <c>CreateScope</c>，避免把请求作用域的上下文拖进长任务。
/// </para>
/// </summary>
public sealed class MediaScanService : IMediaScanService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaLibraryScanner _scanner;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<long, MediaScanProgress> _progress = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private readonly object _gate = new();

    /// <summary>创建扫描编排服务。</summary>
    /// <param name="scopeFactory">作用域工厂：后台任务需要独立创建 DbContext。</param>
    /// <param name="scanner">扫描执行器（transient，只依赖单例的文件系统服务）。</param>
    /// <param name="time">时间源（可选，默认系统时钟）。</param>
    public MediaScanService(IServiceScopeFactory scopeFactory, MediaLibraryScanner scanner, TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _scanner = scanner;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp> StartAsync(long libraryId, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp.Fail("permission denied", 403);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        var lib = await db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        if (lib is null) return Resp.Fail("library not found", RespCodes.NotFound);

        lock (_gate)
        {
            if (_running.ContainsKey(libraryId)) return Resp.Fail("scan already running", RespCodes.Conflict);
            var cts = new CancellationTokenSource();
            _running[libraryId] = cts;
            _progress[libraryId] = new MediaScanProgress { State = "running", StartedAt = _time.GetUtcNow() };
            _ = Task.Run(() => RunAsync(libraryId, lib.Path, lib.Type, cts.Token), CancellationToken.None);
        }
        return Resp.Ok();
    }

    /// <inheritdoc />
    public Task<Resp> StopAsync(long libraryId, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Task.FromResult(Resp.Fail("permission denied", 403));
        if (_running.TryGetValue(libraryId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(Resp.Ok());
        }
        return Task.FromResult(Resp.Fail("scan not running", RespCodes.BadRequest));
    }

    /// <inheritdoc />
    public Task<Resp<MediaScanProgress>> GetProgressAsync(long libraryId, CancellationToken ct = default)
    {
        // 返回副本：进度对象会被后台线程持续写入，直接暴露会读到撕裂的中间态。
        var snapshot = _progress.TryGetValue(libraryId, out var p)
            ? new MediaScanProgress
            {
                State = p.State,
                CurrentPath = p.CurrentPath,
                Found = p.Found,
                Added = p.Added,
                Updated = p.Updated,
                Removed = p.Removed,
                StartedAt = p.StartedAt,
                FinishedAt = p.FinishedAt,
            }
            : new MediaScanProgress();
        return Task.FromResult(Resp<MediaScanProgress>.Ok(snapshot));
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaScanLog>>> ListLogsAsync(long libraryId, int limit, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp<List<MediaScanLog>>.Fail("permission denied", 403);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        var logs = await db.MediaScanLogs.AsNoTracking()
            .Where(l => l.LibraryId == libraryId)
            .OrderByDescending(l => l.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);
        return Resp<List<MediaScanLog>>.Ok(logs);
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaScanTask>>> ListTasksAsync(User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp<List<MediaScanTask>>.Fail("permission denied", 403);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        var libraries = scope.ServiceProvider.GetRequiredService<IMediaLibraryService>();
        var visible = (await libraries.ListAsync(user, ct)).Data ?? [];

        // 历史表按库取"最近一条"：先整表读回再分组，避免 GroupBy+First 这类
        // 关系型提供程序翻译不了的形状（扫描历史量级很小）。
        var logs = await db.MediaScanLogs.AsNoTracking().ToListAsync(ct);
        var lastByLibrary = logs
            .GroupBy(l => l.LibraryId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Id).First());

        var tasks = new List<MediaScanTask>(visible.Count);
        foreach (var lib in visible)
        {
            var running = _running.ContainsKey(lib.Id);
            _progress.TryGetValue(lib.Id, out var live);
            lastByLibrary.TryGetValue(lib.Id, out var last);

            // 状态优先级：正在跑 → 内存进度；否则看最近一次历史的 Error 判完成/取消/失败。
            var state = running ? "running"
                : last is null ? "idle"
                : last.Error is null ? "completed"
                : last.Error == "cancelled" ? "cancelled"
                : "failed";

            tasks.Add(new MediaScanTask
            {
                LibraryId = lib.Id,
                LibraryName = lib.Name,
                Path = lib.Path,
                State = state,
                IsRunning = running,
                CurrentPath = running ? live?.CurrentPath : null,
                Found = running ? live?.Found ?? 0 : 0,
                Added = running ? live?.Added ?? 0 : last?.Added ?? 0,
                Updated = running ? live?.Updated ?? 0 : last?.Updated ?? 0,
                Removed = running ? live?.Removed ?? 0 : last?.Removed ?? 0,
                StartedAt = running ? live?.StartedAt : last?.StartedAt ?? lib.LastScanAt,
                FinishedAt = running ? live?.FinishedAt : last?.FinishedAt,
                Error = last?.Error,
            });
        }

        // 正在运行的排前面，其余按最近一次扫描时间倒序。
        var ordered = tasks
            .OrderByDescending(t => t.IsRunning)
            .ThenByDescending(t => t.StartedAt ?? DateTimeOffset.MinValue)
            .ToList();
        return Resp<List<MediaScanTask>>.Ok(ordered);
    }

    /// <summary>扫描主流程（后台）。</summary>
    private async Task RunAsync(long libraryId, string rootPath, MediaLibraryType libraryType, CancellationToken ct)
    {
        var progress = _progress[libraryId];
        var discovered = new List<MediaFileCandidate>();
        var sidecars = new List<MediaSidecarCandidate>();
        var log = new MediaScanLog { LibraryId = libraryId, StartedAt = _time.GetUtcNow() };

        try
        {
            await _scanner.WalkAsync(rootPath, discovered, progress, ct, sidecars);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            var changes = await _scanner.PersistAsync(
                db, libraryId, rootPath, libraryType, discovered, _time.GetUtcNow(), ct, sidecars);

            log.Added = changes.Added;
            log.Updated = changes.Updated;
            log.Removed = changes.Removed;
            log.FinishedAt = _time.GetUtcNow();
            db.MediaScanLogs.Add(log);

            var lib = await db.MediaLibraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct);
            if (lib is not null)
            {
                lib.LastScanAt = log.FinishedAt;
                lib.UpdatedAt = log.FinishedAt.Value;
            }
            await db.SaveChangesAsync(ct);

            progress.Added = changes.Added;
            progress.Updated = changes.Updated;
            progress.Removed = changes.Removed;
            progress.State = "completed";
            progress.FinishedAt = log.FinishedAt;
        }
        catch (OperationCanceledException)
        {
            progress.State = "cancelled";
            progress.FinishedAt = _time.GetUtcNow();
            log.Error = "cancelled";
            log.FinishedAt = progress.FinishedAt;
            await TryWriteLogAsync(log);
        }
        catch (Exception ex)
        {
            progress.State = "failed";
            progress.FinishedAt = _time.GetUtcNow();
            log.Error = ex.Message;
            log.FinishedAt = progress.FinishedAt;
            await TryWriteLogAsync(log);
        }
        finally
        {
            if (_running.TryRemove(libraryId, out var cts)) cts.Dispose();
        }
    }

    /// <summary>失败/取消时尽力写入扫描历史（独立作用域，避免影响主流程异常）。</summary>
    private async Task TryWriteLogAsync(MediaScanLog log)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            db.MediaScanLogs.Add(log);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // 历史写入失败不影响扫描结果状态。
        }
    }
}
