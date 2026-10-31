using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Services;
using OpenList.Infrastructure.Tasks;

namespace OpenList.Tests;

/// <summary>
/// 任务并发上限（<c>*_task_threads_num</c>）：设置解析、闸门语义、TaskService 排队行为。
/// </summary>
public class TaskConcurrencyTests
{
    /// <summary>可变的并发上限来源，模拟设置表刷新后的快照切换。</summary>
    private sealed class FakeProvider : ITaskConcurrencyProvider
    {
        public FakeProvider(TaskConcurrencyLimits limits) => Current = limits;

        public TaskConcurrencyLimits Current { get; set; }
    }

    private static TaskConcurrencyLimits LimitsOf(params (TaskType Type, int Limit)[] items)
        => new(items.ToDictionary(i => i.Type, i => i.Limit));

    [Test]
    public async Task FromSettings_Fills_Default_For_Missing_And_Invalid_Values()
    {
        var limits = TaskConcurrencyLimits.FromSettings(new Dictionary<string, string>
        {
            ["copy_task_threads_num"] = "3",
            ["move_task_threads_num"] = "not-a-number",
        });

        await Assert.That(limits.LimitOf(TaskType.Copy)).IsEqualTo(3);
        // 非法值退回默认值（不是 0）：写错一个字符不应变成「不限流」。
        await Assert.That(limits.LimitOf(TaskType.Move)).IsEqualTo(TaskConcurrencyLimits.DefaultLimit);
        // 缺键同样走默认值。
        await Assert.That(limits.LimitOf(TaskType.Upload)).IsEqualTo(TaskConcurrencyLimits.DefaultLimit);
        // 没有映射的类型不限。
        await Assert.That(limits.LimitOf(TaskType.Aria2Down)).IsEqualTo(0);
    }

    [Test]
    public async Task FromSettings_Treats_Zero_And_Negative_As_Unlimited()
    {
        var limits = TaskConcurrencyLimits.FromSettings(new Dictionary<string, string>
        {
            ["copy_task_threads_num"] = "0",
            ["upload_task_threads_num"] = "-2",
        });

        await Assert.That(limits.LimitOf(TaskType.Copy)).IsEqualTo(0);
        await Assert.That(limits.LimitOf(TaskType.Upload)).IsEqualTo(0);
    }

    [Test]
    public async Task Every_Setting_Key_Is_Mapped_To_A_Distinct_Task_Type()
    {
        var types = TaskConcurrencyLimits.Keys.Select(k => k.Type).ToList();
        await Assert.That(types.Count).IsEqualTo(8);
        await Assert.That(types.Distinct().Count()).IsEqualTo(8);
        await Assert.That(TaskConcurrencyLimits.Keys.Select(k => k.Key).Distinct().Count()).IsEqualTo(8);
    }

    [Test]
    public async Task Gate_Blocks_Beyond_Limit_And_Releases_On_Dispose()
    {
        var gate = new ConcurrencyGate(1);
        var first = await gate.AcquireAsync(CancellationToken.None);

        var second = gate.AcquireAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        await Assert.That(second.IsCompleted).IsFalse();

        first.Dispose();
        var lease = await second.WaitAsync(TimeSpan.FromSeconds(2));
        lease.Dispose();
    }

    [Test]
    public async Task Gate_Raises_Waiters_When_Limit_Is_Increased()
    {
        var gate = new ConcurrencyGate(1);
        var first = await gate.AcquireAsync(CancellationToken.None);

        var waiting = gate.AcquireAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        await Assert.That(waiting.IsCompleted).IsFalse();

        gate.SetLimit(2);
        var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        lease.Dispose();
        first.Dispose();
    }

    [Test]
    public async Task Gate_Releases_All_Waiters_When_Limit_Turns_Unlimited()
    {
        var gate = new ConcurrencyGate(1);
        var first = await gate.AcquireAsync(CancellationToken.None);

        var a = gate.AcquireAsync(CancellationToken.None).AsTask();
        var b = gate.AcquireAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        await Assert.That(a.IsCompleted).IsFalse();

        gate.SetLimit(0);
        var la = await a.WaitAsync(TimeSpan.FromSeconds(2));
        var lb = await b.WaitAsync(TimeSpan.FromSeconds(2));
        la.Dispose();
        lb.Dispose();
        first.Dispose();
    }

    [Test]
    public async Task Gate_Canceled_Waiter_Does_Not_Leak_A_Slot()
    {
        var gate = new ConcurrencyGate(1);
        var first = await gate.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(cts.Token).AsTask();
        await Task.Delay(50);
        cts.Cancel();

        var canceled = false;
        try { await waiting; }
        catch (OperationCanceledException) { canceled = true; }
        await Assert.That(canceled).IsTrue();

        // 取消的等待者不能吞掉名额：释放第一个后必须能立刻拿到新的。
        first.Dispose();
        var lease = await gate.AcquireAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        lease.Dispose();
    }

    [Test]
    public async Task TaskService_Queues_Tasks_Beyond_Limit()
    {
        var svc = new TaskService(null, new FakeProvider(LimitsOf((TaskType.Copy, 1))));
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var t1 = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            progress.Report(new TaskProgress { Current = 1, Total = 1 });
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var t2 = await svc.CreateAsync(TaskType.Copy, (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 1, Total = 1 });
            return Task.CompletedTask;
        });

        // 上限 1 且第一个还没结束：第二个必须停在等待中，而不是被拒绝或并行执行。
        await Task.Delay(100);
        var queued = await svc.InfoAsync(TaskType.Copy, t2);
        await Assert.That(queued.Data!.State).IsEqualTo(TaskState.Pending);

        release.SetResult(true);
        await Assert.That((await WaitFinishedAsync(svc, t1)).State).IsEqualTo(TaskState.Succeeded);
        await Assert.That((await WaitFinishedAsync(svc, t2)).State).IsEqualTo(TaskState.Succeeded);
    }

    [Test]
    public async Task TaskService_Canceling_Queued_Task_Frees_The_Queue()
    {
        var svc = new TaskService(null, new FakeProvider(LimitsOf((TaskType.Copy, 1))));
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var t1 = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(ct);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var t2 = await svc.CreateAsync(TaskType.Copy, (progress, ct) => Task.Delay(1000, ct));
        await Task.Delay(50);
        var cancel = await svc.CancelAsync(TaskType.Copy, t2);
        await Assert.That(cancel.Code).IsEqualTo(200);
        await Assert.That((await WaitFinishedAsync(svc, t2)).State).IsEqualTo(TaskState.Canceled);

        // 取消排队任务不能把名额一起带走：后续任务仍要能跑起来。
        release.SetResult(true);
        await Assert.That((await WaitFinishedAsync(svc, t1)).State).IsEqualTo(TaskState.Succeeded);

        var t3 = await svc.CreateAsync(TaskType.Copy, (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 1, Total = 1 });
            return Task.CompletedTask;
        });
        await Assert.That((await WaitFinishedAsync(svc, t3)).State).IsEqualTo(TaskState.Succeeded);
    }

    [Test]
    public async Task TaskService_Without_Provider_Does_Not_Queue()
    {
        var svc = new TaskService();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var t1 = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(ct);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var t2 = await svc.CreateAsync(TaskType.Copy, (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 1, Total = 1 });
            return Task.CompletedTask;
        });

        // 未注入并发来源 = 不限流，第二个任务立刻完成（与接线前行为一致）。
        await Assert.That((await WaitFinishedAsync(svc, t2)).State).IsEqualTo(TaskState.Succeeded);
        release.SetResult(true);
        await WaitFinishedAsync(svc, t1);
    }

    private static async Task<TaskInfo> WaitFinishedAsync(TaskService svc, string tid)
    {
        var info = await svc.InfoAsync(TaskType.Copy, tid);
        for (var i = 0; i < 100; i++)
        {
            if (info.Data!.State is not (TaskState.Pending or TaskState.Running)) break;
            await Task.Delay(20);
            info = await svc.InfoAsync(TaskType.Copy, tid);
        }
        return info.Data!;
    }
}
