using OpenList.Media.Services;

namespace OpenList.Tests;

/// <summary>
/// 整库刮削的并发守卫与进度换算。
///
/// 这一层的存在理由：<c>MediaMetadataService</c> 是 Scoped，批量刮削又是 fire-and-forget，
/// 守卫放错地方就会「每次请求一把新锁」——界面上看不出异样，直到 TMDB 配额被双倍消耗。
/// 所以把状态单独抽成单例，并在这里把原子性、阈值、夹取行为钉死。
/// </summary>
public class MediaScrapeCoordinatorTests
{
    [Test]
    public async Task TryBegin_Blocks_Second_Run_Until_End()
    {
        var coordinator = new MediaScrapeCoordinator();

        await Assert.That(coordinator.TryBegin(7, 10)).IsTrue();
        await Assert.That(coordinator.TryBegin(7, 10)).IsFalse();

        coordinator.End(7);
        await Assert.That(coordinator.TryBegin(7, 10)).IsTrue();
    }

    [Test]
    public async Task TryBegin_Is_Atomic_Under_Concurrency()
    {
        var coordinator = new MediaScrapeCoordinator();
        var granted = 0;

        // 32 个线程同时抢同一个库：必须恰好一个成功。
        // 先 TryGetValue 再 TryAdd 的实现会在这里放过多个。
        await Parallel.ForAsync(0, 32, (_, _) =>
        {
            if (coordinator.TryBegin(1, 5)) Interlocked.Increment(ref granted);
            return ValueTask.CompletedTask;
        });

        await Assert.That(granted).IsEqualTo(1);
    }

    [Test]
    public async Task Different_Libraries_Do_Not_Block_Each_Other()
    {
        var coordinator = new MediaScrapeCoordinator();

        await Assert.That(coordinator.TryBegin(1, 5)).IsTrue();
        await Assert.That(coordinator.TryBegin(2, 5)).IsTrue();
    }

    [Test]
    public async Task End_Keeps_Record_So_Last_Failure_Stays_Visible()
    {
        var coordinator = new MediaScrapeCoordinator();
        coordinator.TryBegin(3, 20);
        var run = coordinator.Get(3)!;
        run.RecordFailure("401 Unauthorized");

        coordinator.End(3);

        var after = coordinator.Get(3);
        await Assert.That(after).IsNotNull();
        // 结束的一轮要留在字典里：否则熔断中止后管理台只看到「没在跑」，
        // 完全没有线索解释为什么 pending 一条没少。
        await Assert.That(after!.Running).IsFalse();
        await Assert.That(after.Error).IsEqualTo("401 Unauthorized");
        await Assert.That(after.Failed).IsEqualTo(1);
    }

    [Test]
    public async Task New_Run_After_End_Starts_Clean()
    {
        var coordinator = new MediaScrapeCoordinator();
        coordinator.TryBegin(4, 10);
        coordinator.Get(4)!.RecordFailure("boom");
        coordinator.End(4);

        coordinator.TryBegin(4, 10);

        var run = coordinator.Get(4)!;
        await Assert.That(run.Running).IsTrue();
        await Assert.That(run.Failed).IsEqualTo(0);
        await Assert.That(run.Error).IsNull();
    }

    [Test]
    public async Task Get_Returns_Null_For_Unknown_Library_And_End_Is_NoOp()
    {
        var coordinator = new MediaScrapeCoordinator();

        await Assert.That(coordinator.Get(99)).IsNull();
        coordinator.End(99); // 不该抛
        await Assert.That(coordinator.Get(99)).IsNull();
    }

    [Test]
    public async Task Batch_Size_Is_Capped_But_StartPending_Is_Not()
    {
        var coordinator = new MediaScrapeCoordinator();
        coordinator.TryBegin(5, MediaScrapeCoordinator.MaxBatch + 500);

        var run = coordinator.Get(5)!;
        // 起点记全量（进度换算要用它做被减数），批量上限单独记
        await Assert.That(run.StartPending).IsEqualTo(MediaScrapeCoordinator.MaxBatch + 500);
        await Assert.That(run.BatchSize).IsEqualTo(MediaScrapeCoordinator.MaxBatch);
    }

    [Test]
    public async Task DoneOf_Clamps_Both_Ends()
    {
        var run = new MediaScrapeRun(startPending: 100, batchSize: 100);

        await Assert.That(run.DoneOf(60)).IsEqualTo(40);
        // 运行期间扫描插进来新增了条目：差值为负，进度不能倒着走
        await Assert.That(run.DoneOf(130)).IsEqualTo(0);
        // 上限是 BatchSize，不是全量
        var capped = new MediaScrapeRun(startPending: 5000, batchSize: 2000);
        await Assert.That(capped.DoneOf(0)).IsEqualTo(2000);
    }

    [Test]
    public async Task ShouldAbort_Only_On_Consecutive_Failures()
    {
        var run = new MediaScrapeRun(startPending: 10, batchSize: 10);

        for (var i = 0; i < MediaScrapeCoordinator.AbortAfterConsecutiveFailures; i++)
        {
            await Assert.That(run.ShouldAbort).IsFalse();
            run.RecordFailure($"err {i}");
        }

        await Assert.That(run.ShouldAbort).IsTrue();

        // 一次成功就把连续计数清零：偶发失败不该把整批提前掐掉
        run.RecordSuccess();
        await Assert.That(run.ShouldAbort).IsFalse();
        // 但累计失败数保留，用于界面上的「N failed」
        await Assert.That(run.Failed).IsEqualTo(MediaScrapeCoordinator.AbortAfterConsecutiveFailures);
    }
}
