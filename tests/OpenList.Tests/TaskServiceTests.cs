using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// TaskService tests: create, progress, cancel, retry, done/undone lists, clear.
/// </summary>
public class TaskServiceTests
{
    [Test]
    public async Task CreateAsync_Returns_TaskId_And_Runs_To_Completion()
    {
        var svc = new TaskService();
        var tid = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 50, Total = 100 });
            await Task.Delay(10, ct);
            progress.Report(new TaskProgress { Current = 100, Total = 100 });
        });
        await Assert.That(tid).Contains("copy-");

        // Wait for completion
        await Task.Delay(200);
        var info = await svc.InfoAsync(TaskType.Copy, tid);
        await Assert.That(info.Code).IsEqualTo(200);
        await Assert.That(info.Data!.State).IsEqualTo("succeeded");
        await Assert.That(info.Data.Progress).IsEqualTo(100);
    }

    [Test]
    public async Task UndoneAsync_Returns_Pending_And_Running_Tasks()
    {
        var svc = new TaskService();
        var t1 = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 1, Total = 100 });
            await Task.Delay(500, ct);
        });
        var t2 = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 1, Total = 100 });
            await Task.Delay(500, ct);
        });

        await Task.Delay(50);
        var undone = await svc.UndoneAsync(TaskType.Copy);
        await Assert.That(undone.Data!.Count).IsEqualTo(2);

        // Cancel both to clean up
        await svc.CancelAsync(TaskType.Copy, t1);
        await svc.CancelAsync(TaskType.Copy, t2);
    }

    [Test]
    public async Task DoneAsync_Returns_Finished_Tasks()
    {
        var svc = new TaskService();
        var tid = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 100, Total = 100 });
        });
        await Task.Delay(200);

        var done = await svc.DoneAsync(TaskType.Copy);
        await Assert.That(done.Data!.Count).IsEqualTo(1);
        await Assert.That(done.Data[0].State).IsEqualTo("succeeded");
    }

    [Test]
    public async Task CancelAsync_Stops_Running_Task()
    {
        var svc = new TaskService();
        var tid = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 1, Total = 100 });
            await Task.Delay(2000, ct);
        });
        await Task.Delay(50);

        var r = await svc.CancelAsync(TaskType.Copy, tid);
        await Assert.That(r.Code).IsEqualTo(200);
        await Task.Delay(100);

        var info = await svc.InfoAsync(TaskType.Copy, tid);
        await Assert.That(info.Data!.State).IsEqualTo("canceled");
    }

    [Test]
    public async Task CancelAsync_Returns_Error_For_Unknown_Task()
    {
        var svc = new TaskService();
        var r = await svc.CancelAsync(TaskType.Copy, "nonexistent");
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task CancelAsync_Returns_Error_For_Finished_Task()
    {
        var svc = new TaskService();
        var tid = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 100, Total = 100 });
        });
        await Task.Delay(200);

        var r = await svc.CancelAsync(TaskType.Copy, tid);
        await Assert.That(r.Code).IsEqualTo(400);
    }

    [Test]
    public async Task DeleteAsync_Removes_Task()
    {
        var svc = new TaskService();
        var tid = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 100, Total = 100 });
        });
        await Task.Delay(200);

        var r = await svc.DeleteAsync(TaskType.Copy, tid);
        await Assert.That(r.Code).IsEqualTo(200);

        var info = await svc.InfoAsync(TaskType.Copy, tid);
        await Assert.That(info.Code).IsEqualTo(404);
    }

    [Test]
    public async Task ClearAsync_Removes_Finished_Tasks_Only()
    {
        var svc = new TaskService();
        // Finished task
        var t1 = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 100, Total = 100 });
        });
        // Running task
        var t2 = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 1, Total = 100 });
            await Task.Delay(2000, ct);
        });
        await Task.Delay(200);

        var r = await svc.ClearAsync(TaskType.Copy);
        await Assert.That(r.Code).IsEqualTo(200);

        var done = await svc.DoneAsync(TaskType.Copy);
        await Assert.That(done.Data!.Count).IsEqualTo(0);

        var undone = await svc.UndoneAsync(TaskType.Copy);
        await Assert.That(undone.Data!.Count).IsEqualTo(1);

        await svc.CancelAsync(TaskType.Copy, t2);
    }

    [Test]
    public async Task RetryAsync_Reruns_Failed_Task()
    {
        var svc = new TaskService();
        var attempt = 0;
        var tid = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            attempt++;
            if (attempt == 1) throw new Exception("first attempt fails");
            progress.Report(new TaskProgress { Current = 100, Total = 100 });
        });
        await Task.Delay(200);

        var info = await svc.InfoAsync(TaskType.Copy, tid);
        await Assert.That(info.Data!.State).IsEqualTo("failed");
        await Assert.That(info.Data.Error).IsEqualTo("first attempt fails");

        var r = await svc.RetryAsync(TaskType.Copy, tid);
        await Assert.That(r.Code).IsEqualTo(200);
        await Task.Delay(200);

        var info2 = await svc.InfoAsync(TaskType.Copy, tid);
        await Assert.That(info2.Data!.State).IsEqualTo("succeeded");
    }

    [Test]
    public async Task RetryAsync_Returns_Error_For_Running_Task()
    {
        var svc = new TaskService();
        var tid = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 1, Total = 100 });
            await Task.Delay(2000, ct);
        });
        await Task.Delay(50);

        var r = await svc.RetryAsync(TaskType.Copy, tid);
        await Assert.That(r.Code).IsEqualTo(400);

        await svc.CancelAsync(TaskType.Copy, tid);
    }

    [Test]
    public async Task InfoAsync_Returns_NotFound_For_Unknown_Task()
    {
        var svc = new TaskService();
        var r = await svc.InfoAsync(TaskType.Copy, "nonexistent");
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task Tasks_Are_Separated_By_Type()
    {
        var svc = new TaskService();
        var copyTid = await svc.CreateAsync(TaskType.Copy, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 100, Total = 100 });
        });
        var moveTid = await svc.CreateAsync(TaskType.Move, async (progress, ct) =>
        {
            progress.Report(new TaskProgress { Current = 100, Total = 100 });
        });
        await Task.Delay(200);

        var copyDone = await svc.DoneAsync(TaskType.Copy);
        await Assert.That(copyDone.Data!.Count).IsEqualTo(1);

        var moveDone = await svc.DoneAsync(TaskType.Move);
        await Assert.That(moveDone.Data!.Count).IsEqualTo(1);

        // Cross-type lookup fails
        var info = await svc.InfoAsync(TaskType.Copy, moveTid);
        await Assert.That(info.Code).IsEqualTo(404);
    }
}
