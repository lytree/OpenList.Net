using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Tasks;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 后台任务服务：内存中跟踪运行态，并把状态快照持久化到 <c>x_tasks</c>。
/// 按 id 跟踪复制/上传/传输/下载/解压/移动/离线下载等任务。
/// 对应 Go 参考实现 <c>pkg/task</c> 任务管理器。
/// <para>
/// 并发上限由 <c>*_task_threads_num</c> 设置提供（见 <see cref="TaskConcurrencyLimits"/>）：
/// 每个任务类型一条队列，超出上限的任务保持 <see cref="TaskState.Pending"/>（等待中），
/// 拿到名额才转 <see cref="TaskState.Running"/>。未注入并发提供者时不限流。
/// </para>
/// <para>
/// 持久化是可选的（未注入 <see cref="IServiceScopeFactory"/> 时纯内存，单测即走这条路径）。
/// 工作负载是闭包，不可跨进程恢复，因此重启只会把未完成的任务标记为
/// <see cref="TaskState.Errored"/>（见 <see cref="RestoreAsync"/>），不会尝试续跑。
/// </para>
/// </summary>
public sealed class TaskService : ITaskService
{
    /// <summary>重启时未完成任务统一使用的错误信息。</summary>
    private const string RestartErrorMessage = "interrupted by server restart";

    private readonly TimeProvider _time;
    private readonly ITaskConcurrencyProvider? _concurrency;
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger<TaskService>? _logger;
    private readonly ConcurrentDictionary<string, TaskRecord> _tasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();
    private readonly ConcurrentDictionary<TaskType, ConcurrencyGate> _gates = new();
    private readonly object _restoreGate = new();
    private Task? _restore;
    private int _seq;

    /// <summary>
    /// 创建任务服务实例。
    /// </summary>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    /// <param name="concurrency">并发上限来源（可选；为空时所有类型都不限并发，与接线前行为一致）。</param>
    /// <param name="scopes">作用域工厂（可选；为空时不持久化任务状态）。</param>
    /// <param name="logger">日志（持久化失败只记录，不影响任务本身）。</param>
    public TaskService(
        TimeProvider? time = null,
        ITaskConcurrencyProvider? concurrency = null,
        IServiceScopeFactory? scopes = null,
        ILogger<TaskService>? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _concurrency = concurrency;
        _scopes = scopes;
        _logger = logger;
    }

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
        await PersistAsync(rec, remove: true);
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
        await PersistAsync(rec);
        return Resp.Ok();
    }

    /// <summary>
    /// 清空某类型下所有已结束的任务。
    /// </summary>
    public async Task<Resp> ClearAsync(TaskType type, CancellationToken ct = default)
    {
        foreach (var kvp in _tasks)
        {
            if (kvp.Value.Info.Type == type && IsFinished(kvp.Value.Info.State) && _tasks.TryRemove(kvp.Key, out var rec))
                await PersistAsync(rec, remove: true);
        }
        return Resp.Ok();
    }

    /// <summary>
    /// 清空某类型下所有已成功的任务。
    /// </summary>
    public async Task<Resp> ClearSucceededAsync(TaskType type, CancellationToken ct = default)
    {
        foreach (var kvp in _tasks)
        {
            if (kvp.Value.Info.Type == type && kvp.Value.Info.State == TaskState.Succeeded && _tasks.TryRemove(kvp.Key, out var rec))
                await PersistAsync(rec, remove: true);
        }
        return Resp.Ok();
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
    /// 重试单个任务（仅限已失败或已取消、且工作负载仍在本进程内的任务）。
    /// </summary>
    public async Task<Resp> RetryAsync(TaskType type, string tid, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(tid, out var rec) || rec.Info.Type != type)
            return Resp.Fail("task not found", RespCodes.NotFound);
        if (rec.Info.State != TaskState.Errored && rec.Info.State != TaskState.Canceled)
            return Resp.Fail("only failed or canceled tasks can be retried", RespCodes.BadRequest);
        if (rec.Work is null)
            return Resp.Fail("task cannot be retried after server restart", RespCodes.BadRequest);

        // 重置状态并重新执行原始工作。
        rec.Info.State = TaskState.Pending;
        rec.Info.Error = null;
        rec.Info.Progress = 0;
        rec.Info.EndTime = null;
        rec.Info.CompletedAt = null;

        // 上一轮的取消令牌源可能已被取消并 Dispose（取消过的任务、或 RunAsync 收尾时移出登记表），
        // 重试必须换一个新的，否则任务体拿到已取消的令牌会立刻退出。重复 Dispose 是幂等的。
        rec.Cts?.Dispose();
        var cts = new CancellationTokenSource();
        rec.Cts = cts;
        _cts[tid] = cts;

        await PersistAsync(rec);
        ScheduleRun(tid, rec);
        return Resp.Ok();
    }

    /// <summary>
    /// 创建一个后台任务并立即启动执行，返回生成的任务 id（格式：<c>&lt;type&gt;-&lt;序号&gt;</c>）。
    /// </summary>
    /// <param name="type">任务类型。</param>
    /// <param name="work">任务工作负载（通过 <see cref="IProgress{T}"/> 上报进度，可响应取消）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新建任务的任务 id。</returns>
    public async Task<string> CreateAsync(TaskType type, Func<IProgress<TaskProgress>, CancellationToken, Task> work, CancellationToken ct = default, long ownerId = 0)
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
                OwnerId = ownerId,
                CreatedAt = now,
                StartTime = now,
            },
            Work = work,
            Cts = cts,
        };
        _tasks[tid] = rec;

        // 先落库再调度：任务通常在毫秒级完成，先写一条 Pending 记录能保证
        // 「创建成功但立即结束」的任务也不会漏掉；后续状态转换会覆盖这一行。
        await PersistAsync(rec);
        ScheduleRun(tid, rec);
        return tid;
    }

    /// <summary>查询任务创建者；任务不存在返回 null。</summary>
    public long? OwnerOf(string tid)
        => _tasks.TryGetValue(tid, out var rec) ? rec.Info.OwnerId : null;

    /// <summary>
    /// 启动期恢复持久化任务：读回历史记录，并把未完成的任务标记为出错。
    /// </summary>
    public Task RestoreAsync(CancellationToken ct = default)
    {
        lock (_restoreGate)
            return _restore ??= RestoreCoreAsync(ct);
    }

    /// <summary>
    /// 把任务体丢到线程池执行。
    /// <para>
    /// 不能直接 <c>_ = RunAsync(...)</c>：复制/移动的任务体是同步 IO（<c>File.Copy</c>），
    /// 在第一个真正的 await 之前整段都在调用线程上跑完 —— 于是 <see cref="CreateAsync"/>
    /// 会一直阻塞到复制结束，批量复制的 6 个任务被顺序串起来，并发上限根本没机会生效，
    /// 同时把 <c>fs/copy</c> 的 HTTP 请求也一起卡住。换成线程池调度后：
    /// 创建立即返回、并发闸门真正生效、请求线程不被后台 IO 占用。
    /// </para>
    /// </summary>
    private void ScheduleRun(string tid, TaskRecord rec)
        => _ = Task.Run(() => RunAsync(tid, rec));

    /// <summary>
    /// 执行任务工作负载并更新状态：成功 /（取消时）已取消 /（异常时）出错，结束后记录结束时间。
    /// <para>
    /// 先按任务类型申请并发名额，期间状态停在 <see cref="TaskState.Pending"/>；
    /// 拿到名额才开始跑并把状态切成 <see cref="TaskState.Running"/>。名额在 finally 归还，
    /// 无论成功、失败还是取消都不会泄漏（漏一个名额该类型就永久少一条执行通道）。
    /// </para>
    /// </summary>
    private async Task RunAsync(string tid, TaskRecord rec)
    {
        var progress = new SynchronousProgress<TaskProgress>(p =>
        {
            rec.Info.Progress = p.Current;
            rec.Info.Total = p.Total;
            if (rec.Info.State == TaskState.Pending) rec.Info.State = TaskState.Running;
            PersistProgressThrottled(rec);
        });

        var token = rec.Cts?.Token ?? CancellationToken.None;
        ConcurrencyGate.Lease? lease = null;
        try
        {
            lease = await GateFor(rec.Info.Type).AcquireAsync(token).ConfigureAwait(false);
            rec.Info.State = TaskState.Running;
            rec.Info.StartTime = _time.GetUtcNow();
            await PersistAsync(rec);
            await rec.Work!(progress, token);
            rec.Info.State = TaskState.Succeeded;
            rec.Info.Progress = rec.Info.Total > 0 ? rec.Info.Total : rec.Info.Progress;
        }
        catch (OperationCanceledException)
        {
            // 包含「排队等名额时被取消」：此时任务从未真正开始，直接记为已取消。
            rec.Info.State = TaskState.Canceled;
        }
        catch (Exception ex)
        {
            rec.Info.State = TaskState.Errored;
            rec.Info.Error = ex.Message;
        }
        finally
        {
            lease?.Dispose();
            rec.Info.EndTime = _time.GetUtcNow();
            rec.Info.CompletedAt = rec.Info.EndTime;
            _cts.TryRemove(tid, out _);
            // 已删除的任务不再回写：否则并发删除之后迟到的收尾写入会把记录插回去。
            await PersistAsync(rec);
        }
    }

    /// <summary>
    /// 进度快照按 1 秒节流落库：进度回调随分块拷贝频繁触发，逐次写库会把 IO 拖垮；
    /// 每次状态转换另有独立写入，因此丢掉中间快照不影响最终状态。
    /// </summary>
    private void PersistProgressThrottled(TaskRecord rec)
    {
        if (_scopes is null || rec.Removed) return;
        var now = _time.GetUtcNow();
        if (now - rec.LastPersistedAt < TimeSpan.FromSeconds(1)) return;
        rec.LastPersistedAt = now;
        // 回调是同步的（SynchronousProgress），这里必须等写完再返回，
        // 否则同一任务的多次写会乱序 —— 旧快照可能后到并覆盖新状态。
        PersistAsync(rec).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 写（或删除）一条持久化任务记录。持久化失败只记录日志：任务本身仍在内存中正常运行，
    /// 不能因为库写不进去就让一个已经开始的复制/解压任务失败。
    /// </summary>
    private async Task PersistAsync(TaskRecord rec, bool remove = false)
    {
        if (_scopes is null) return;
        if (remove) rec.Removed = true;
        else if (rec.Removed) return;
        // 每次状态写入顺带重置进度节流基线：刚写过的状态不需要进度回调再补写一次。
        if (!remove) rec.LastPersistedAt = _time.GetUtcNow();

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            var row = await db.BackgroundTasks.FirstOrDefaultAsync(t => t.Id == rec.Info.Id).ConfigureAwait(false);
            // 查询期间任务可能刚被删除：此时不能再写回，否则会把已删的行重新插回库里。
            if (rec.Removed && !remove) return;
            if (remove)
            {
                if (row is not null)
                {
                    db.BackgroundTasks.Remove(row);
                    await db.SaveChangesAsync().ConfigureAwait(false);
                }
                return;
            }

            if (row is null)
            {
                row = new BackgroundTask { Id = rec.Info.Id };
                db.BackgroundTasks.Add(row);
            }

            row.Type = rec.Info.Type;
            row.Name = rec.Info.Name;
            row.State = rec.Info.State;
            row.Progress = rec.Info.Progress;
            row.Total = rec.Info.Total;
            row.CreatedAt = rec.Info.CreatedAt;
            row.StartTime = rec.Info.StartTime;
            row.EndTime = rec.Info.EndTime;
            row.Error = rec.Info.Error;
            row.Creator = rec.Info.Creator;
            row.CreatorRole = rec.Info.CreatorRole;
            row.OwnerId = rec.Info.OwnerId;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "任务 {Tid} 持久化失败（任务本身不受影响）", rec.Info.Id);
        }
    }

    /// <summary>
    /// 恢复持久化任务：未完成的标记为出错，并恢复任务序号以免新任务复用历史 id。
    /// </summary>
    private async Task RestoreCoreAsync(CancellationToken ct)
    {
        if (_scopes is null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            var rows = await db.BackgroundTasks.ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count == 0) return;

            var now = _time.GetUtcNow();
            var maxSeq = 0;
            var changed = 0;
            foreach (var row in rows)
            {
                var dash = row.Id.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(row.Id[(dash + 1)..], out var n) && n > maxSeq)
                    maxSeq = n;

                if (!IsFinished(row.State))
                {
                    row.State = TaskState.Errored;
                    row.Error = RestartErrorMessage;
                    row.EndTime = now;
                    changed++;
                }

                // 历史任务的工作负载已随进程消失：Work 为 null，retry 会明确拒绝。
                _tasks[row.Id] = new TaskRecord { Info = MapToInfo(row) };
            }

            if (changed > 0)
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // 任务 id 是「类型-自增序号」，序号如果从 0 重新开始就会与历史行撞 id
            // （撞上后新任务的写入会覆盖历史记录）。恢复时把序号推进到历史最大值。
            if (maxSeq > _seq) _seq = maxSeq;

            _logger?.LogInformation(
                "已恢复 {Count} 条任务记录，其中 {Interrupted} 条未完成任务标记为失败（服务重启）。",
                rows.Count, changed);
        }
        catch (Exception ex)
        {
            // 恢复失败不应阻塞启动：大不了这次启动没有历史任务列表。
            _logger?.LogWarning(ex, "恢复持久化任务失败");
        }
    }

    /// <summary>持久化行 → 任务信息。</summary>
    private static TaskInfo MapToInfo(BackgroundTask row) => new()
    {
        Id = row.Id,
        Type = row.Type,
        Name = row.Name,
        State = row.State,
        Progress = row.Progress,
        Total = row.Total,
        StartTime = row.StartTime,
        EndTime = row.EndTime,
        Error = row.Error,
        Creator = row.Creator,
        CreatorRole = row.CreatorRole,
        OwnerId = row.OwnerId,
        CreatedAt = row.CreatedAt,
        CompletedAt = row.EndTime,
    };

    /// <summary>
    /// 取某任务类型的并发闸门，并按最新设置同步上限。
    /// 上限没变时只做一次无锁读取（<see cref="ConcurrencyGate.Limit"/>），不产生锁竞争。
    /// </summary>
    private ConcurrencyGate GateFor(TaskType type)
    {
        var gate = _gates.GetOrAdd(type, static _ => new ConcurrencyGate(0));
        if (_concurrency is null) return gate;

        var target = Math.Max(0, _concurrency.Current.LimitOf(type));
        if (gate.Limit != target) gate.SetLimit(target);
        return gate;
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
        /// <summary>工作负载；从持久化恢复的历史任务为 null（无法续跑/重试）。</summary>
        public Func<IProgress<TaskProgress>, CancellationToken, Task>? Work { get; init; }
        public CancellationTokenSource? Cts { get; set; }
        /// <summary>下一次允许写进度快照的时间（1 秒节流）。</summary>
        public DateTimeOffset LastPersistedAt { get; set; }
        /// <summary>已被删除：迟到的状态写入不得把该行重新插回库里。</summary>
        public volatile bool Removed;
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
