using System.Collections.Concurrent;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// In-memory background task service with progress tracking.
/// Tracks copy/upload/transfer/download/decompress/move tasks by id.
/// Mirrors OpenList Go <c>pkg/task</c> task manager.
/// </summary>
public sealed class TaskService : ITaskService
{
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TaskRecord> _tasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();
    private int _seq;

    public TaskService(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public Task<Resp<TaskInfo>> InfoAsync(TaskType type, string tid, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(tid, out var rec) || rec.Info.Type != type)
            return Task.FromResult(Resp<TaskInfo>.Fail("task not found", RespCodes.NotFound));
        return Task.FromResult(Resp<TaskInfo>.Ok(rec.Info));
    }

    public Task<Resp<List<TaskInfo>>> DoneAsync(TaskType type, CancellationToken ct = default)
    {
        var finished = new HashSet<TaskState> { TaskState.Succeeded, TaskState.Errored, TaskState.Canceled };
        var list = _tasks.Values
            .Where(r => r.Info.Type == type && finished.Contains(r.Info.State))
            .OrderByDescending(r => r.Info.EndTime ?? DateTimeOffset.MaxValue)
            .Select(r => r.Info)
            .ToList();
        return Task.FromResult(Resp<List<TaskInfo>>.Ok(list));
    }

    public Task<Resp<List<TaskInfo>>> UndoneAsync(TaskType type, CancellationToken ct = default)
    {
        var unfinished = new HashSet<TaskState> { TaskState.Pending, TaskState.Running, TaskState.Canceling };
        var list = _tasks.Values
            .Where(r => r.Info.Type == type && unfinished.Contains(r.Info.State))
            .OrderBy(r => r.Info.StartTime ?? r.Info.CreatedAt)
            .Select(r => r.Info)
            .ToList();
        return Task.FromResult(Resp<List<TaskInfo>>.Ok(list));
    }

    public async Task<Resp> DeleteAsync(TaskType type, string tid, CancellationToken ct = default)
    {
        if (!_tasks.TryRemove(tid, out var rec) || rec.Info.Type != type)
            return Resp.Fail("task not found", RespCodes.NotFound);
        await CancelInternalAsync(tid);
        return Resp.Ok();
    }

    public async Task<Resp> CancelAsync(TaskType type, string tid, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(tid, out var rec) || rec.Info.Type != type)
            return Resp.Fail("task not found", RespCodes.NotFound);
        if (IsFinished(rec.Info.State))
            return Resp.Fail("task already finished", RespCodes.BadRequest);
        rec.Info.State = TaskState.Canceling;
        await CancelInternalAsync(tid);
        rec.Info.State = TaskState.Canceled;
        rec.Info.EndTime = _time.GetUtcNow();
        rec.Info.CompletedAt = rec.Info.EndTime;
        return Resp.Ok();
    }

    public Task<Resp> ClearAsync(TaskType type, CancellationToken ct = default)
    {
        foreach (var kvp in _tasks)
        {
            if (kvp.Value.Info.Type == type && IsFinished(kvp.Value.Info.State))
                _tasks.TryRemove(kvp.Key, out _);
        }
        return Task.FromResult(Resp.Ok());
    }

    public Task<Resp> ClearSucceededAsync(TaskType type, CancellationToken ct = default)
    {
        foreach (var kvp in _tasks)
        {
            if (kvp.Value.Info.Type == type && kvp.Value.Info.State == TaskState.Succeeded)
                _tasks.TryRemove(kvp.Key, out _);
        }
        return Task.FromResult(Resp.Ok());
    }

    public async Task<Resp> RetryFailedAsync(TaskType type, CancellationToken ct = default)
    {
        var failed = _tasks.Values
            .Where(r => r.Info.Type == type && (r.Info.State == TaskState.Errored || r.Info.State == TaskState.Canceled))
            .Select(r => r.Info.Id)
            .ToList();
        foreach (var tid in failed)
            await RetryAsync(type, tid, ct);
        return Resp.Ok();
    }

    public async Task<Resp<Dictionary<string, string>>> CancelSomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        foreach (var tid in tids)
        {
            var r = await CancelAsync(type, tid, ct);
            if (r.Code != 200) errors[tid] = r.Message;
        }
        return Resp<Dictionary<string, string>>.Ok(errors);
    }

    public async Task<Resp<Dictionary<string, string>>> DeleteSomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        foreach (var tid in tids)
        {
            var r = await DeleteAsync(type, tid, ct);
            if (r.Code != 200) errors[tid] = r.Message;
        }
        return Resp<Dictionary<string, string>>.Ok(errors);
    }

    public async Task<Resp<Dictionary<string, string>>> RetrySomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        foreach (var tid in tids)
        {
            var r = await RetryAsync(type, tid, ct);
            if (r.Code != 200) errors[tid] = r.Message;
        }
        return Resp<Dictionary<string, string>>.Ok(errors);
    }

    public async Task<Resp> RetryAsync(TaskType type, string tid, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(tid, out var rec) || rec.Info.Type != type)
            return Resp.Fail("task not found", RespCodes.NotFound);
        if (rec.Info.State != TaskState.Errored && rec.Info.State != TaskState.Canceled)
            return Resp.Fail("only failed or canceled tasks can be retried", RespCodes.BadRequest);

        // Reset state and re-run the original work.
        rec.Info.State = TaskState.Pending;
        rec.Info.Error = null;
        rec.Info.Progress = 0;
        rec.Info.EndTime = null;
        rec.Info.CompletedAt = null;
        await RunAsync(tid, rec);
        return Resp.Ok();
    }

    public Task<string> CreateAsync(TaskType type, Func<IProgress<TaskProgress>, CancellationToken, Task> work, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _seq).ToString();
        var tid = type.ToString().ToLowerInvariant() + "-" + id;
        var cts = new CancellationTokenSource();
        _cts[tid] = cts;

        var now = _time.GetUtcNow();
        var rec = new TaskRecord
        {
            Info = new TaskInfo
            {
                Id = tid,
                Type = type,
                Name = type.ToString(),
                State = TaskState.Pending,
                CreatedAt = now,
                StartTime = now,
            },
            Work = work,
            Cts = cts,
        };
        _tasks[tid] = rec;

        _ = RunAsync(tid, rec);
        return Task.FromResult(tid);
    }

    private async Task RunAsync(string tid, TaskRecord rec)
    {
        var progress = new Progress<TaskProgress>(p =>
        {
            rec.Info.Progress = p.Current;
            rec.Info.Total = p.Total;
            if (rec.Info.State == TaskState.Pending) rec.Info.State = TaskState.Running;
        });

        try
        {
            rec.Info.State = TaskState.Running;
            await rec.Work(progress, rec.Cts.Token);
            rec.Info.State = TaskState.Succeeded;
            rec.Info.Progress = rec.Info.Total > 0 ? rec.Info.Total : rec.Info.Progress;
        }
        catch (OperationCanceledException)
        {
            rec.Info.State = TaskState.Canceled;
        }
        catch (Exception ex)
        {
            rec.Info.State = TaskState.Errored;
            rec.Info.Error = ex.Message;
        }
        finally
        {
            rec.Info.EndTime = _time.GetUtcNow();
            rec.Info.CompletedAt = rec.Info.EndTime;
            _cts.TryRemove(tid, out _);
        }
    }

    private static bool IsFinished(TaskState s)
        => s == TaskState.Succeeded || s == TaskState.Errored || s == TaskState.Canceled;

    private Task CancelInternalAsync(string tid)
    {
        if (_cts.TryRemove(tid, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        return Task.CompletedTask;
    }

    private sealed class TaskRecord
    {
        public required TaskInfo Info { get; init; }
        public required Func<IProgress<TaskProgress>, CancellationToken, Task> Work { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }
}
