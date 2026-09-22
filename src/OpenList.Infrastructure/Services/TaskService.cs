using System.Collections.Concurrent;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 内存型后台任务服务，带进度追踪。按 id 跟踪复制/上传/传输/下载/解压/移动等任务。
/// 对应 Go 参考实现 <c>pkg/task</c> 任务管理器。
/// </summary>
public sealed class TaskService : ITaskService
{
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TaskRecord> _tasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();
    private int _seq;

    /// <summary>
    /// 创建任务服务实例。
    /// </summary>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public TaskService(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// 按 id 获取某个类型的任务信息。
    /// </summary>
    public Task<Resp<TaskInfo>> InfoAsync(TaskType type, string tid, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(tid, out var rec) || rec.Info.Type != type)
            return Task.FromResult(Resp<TaskInfo>.Fail("task not found", RespCodes.NotFound));
        return Task.FromResult(Resp<TaskInfo>.Ok(rec.Info));
    }

    /// <summary>
    /// 列出某类型下已结束（成功/失败/取消）的任务，按结束时间倒序。
    /// </summary>
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

    /// <summary>
    /// 列出某类型下未结束（等待/运行中/取消中）的任务，按创建时间升序。
    /// </summary>
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

    /// <summary>
    /// 删除某任务（同时取消其底层执行）。
    /// </summary>
    public async Task<Resp> DeleteAsync(TaskType type, string tid, CancellationToken ct = default)
    {
        if (!_tasks.TryRemove(tid, out var rec) || rec.Info.Type != type)
            return Resp.Fail("task not found", RespCodes.NotFound);
        await CancelInternalAsync(tid);
        return Resp.Ok();
    }

    /// <summary>
    /// 取消某任务：置为取消中，取消底层工作，最终标记为已取消。
    /// </summary>
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

    /// <summary>
    /// 清空某类型下所有已结束的任务。
    /// </summary>
    public Task<Resp> ClearAsync(TaskType type, CancellationToken ct = default)
    {
        foreach (var kvp in _tasks)
        {
            if (kvp.Value.Info.Type == type && IsFinished(kvp.Value.Info.State))
                _tasks.TryRemove(kvp.Key, out _);
        }
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 清空某类型下所有已成功的任务。
    /// </summary>
    public Task<Resp> ClearSucceededAsync(TaskType type, CancellationToken ct = default)
    {
        foreach (var kvp in _tasks)
        {
            if (kvp.Value.Info.Type == type && kvp.Value.Info.State == TaskState.Succeeded)
                _tasks.TryRemove(kvp.Key, out _);
        }
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 重试某类型下所有已失败或已取消的任务。
    /// </summary>
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

    /// <summary>
    /// 批量取消任务；返回各任务 id 对应出现的错误（空字典表示全部成功）。
    /// </summary>
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

    /// <summary>
    /// 批量删除任务；返回各任务 id 对应出现的错误（空字典表示全部成功）。
    /// </summary>
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

    /// <summary>
    /// 批量重试任务；返回各任务 id 对应出现的错误（空字典表示全部成功）。
    /// </summary>
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

    /// <summary>
    /// 重试单个任务（仅限已失败或已取消的任务）：重置状态后重新执行其工作负载。
    /// </summary>
    public async Task<Resp> RetryAsync(TaskType type, string tid, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(tid, out var rec) || rec.Info.Type != type)
            return Resp.Fail("task not found", RespCodes.NotFound);
        if (rec.Info.State != TaskState.Errored && rec.Info.State != TaskState.Canceled)
            return Resp.Fail("only failed or canceled tasks can be retried", RespCodes.BadRequest);

        // 重置状态并重新执行原始工作。
        rec.Info.State = TaskState.Pending;
        rec.Info.Error = null;
        rec.Info.Progress = 0;
        rec.Info.EndTime = null;
        rec.Info.CompletedAt = null;
        await RunAsync(tid, rec);
        return Resp.Ok();
    }

    /// <summary>
    /// 创建一个后台任务并立即启动执行，返回生成的任务 id（格式：<c>&lt;type&gt;-&lt;序号&gt;</c>）。
    /// </summary>
    /// <param name="type">任务类型。</param>
    /// <param name="work">任务工作负载（通过 <see cref="IProgress{T}"/> 上报进度，可响应取消）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新建任务的任务 id。</returns>
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

    /// <summary>
    /// 执行任务工作负载并更新状态：成功 /（取消时）已取消 /（异常时）出错，结束后记录结束时间。
    /// </summary>
    private async Task RunAsync(string tid, TaskRecord rec)
    {
        var progress = new SynchronousProgress<TaskProgress>(p =>
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

    /// <summary>判断任务状态是否为已结束。</summary>
    private static bool IsFinished(TaskState s)
        => s == TaskState.Succeeded || s == TaskState.Errored || s == TaskState.Canceled;

    /// <summary>取消并释放任务的取消令牌源。</summary>
    private Task CancelInternalAsync(string tid)
    {
        if (_cts.TryRemove(tid, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        return Task.CompletedTask;
    }

    /// <summary>内部任务记录：任务信息 + 工作负载 + 取消令牌源。</summary>
    private sealed class TaskRecord
    {
        public required TaskInfo Info { get; init; }
        public required Func<IProgress<TaskProgress>, CancellationToken, Task> Work { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    /// <summary>
    /// 同步进度上报器：<see cref="IProgress{T}.Report"/> 返回时回调必已执行完毕。
    /// </summary>
    /// <remarks>
    /// 此处不能使用 <see cref="Progress{T}"/>。它在构造时捕获 <c>SynchronizationContext</c>，
    /// 服务端/测试场景没有该上下文，于是退回线程池投递，使 <c>Report</c> 变成即发即忘：
    /// 任务体已返回而回调尚未执行，<see cref="RunAsync"/> 收尾时读到的 <c>Total</c> 仍为 0，
    /// 成功任务的进度被记为 0。该竞态在 CPU 较少的 CI runner 上稳定复现
    /// （见测试 <c>CreateAsync_Returns_TaskId_And_Runs_To_Completion</c>）。
    /// </remarks>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}