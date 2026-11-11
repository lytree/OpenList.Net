using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// 任务持久化（表 <c>x_tasks</c>）。
///
/// 守的是「重启后 /api/task 还能看到历史任务」这条链路：
/// <list type="number">
///   <item>任务状态快照确实落库（创建即写入，状态转换覆盖）；</item>
///   <item>重启时仍未完成的任务被标记为失败，而不是永远停在「执行中」；</item>
///   <item>任务序号从历史最大值接续，新任务不会复用历史 id 并覆盖旧记录。</item>
/// </list>
/// 每个用例用一次性**文件**库（内存库跨连接不共享，且 <c>Exists()</c> 语义失真）。
/// </summary>
public class TaskPersistenceTests
{
    [Test]
    public async Task Restart_Marks_Unfinished_Task_Failed_And_Keeps_Succeeded_History()
    {
        var path = NewDbPath();
        var provider = NewProvider(path);
        try
        {
            await EnsureSchemaAsync(provider);
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();

            // 第一轮进程：一个永不结束的任务（模拟重启时仍在跑）。
            var first = new TaskService(scopes: scopes);
            var interrupted = await first.CreateAsync(TaskType.Copy, (progress, ct) =>
                Task.Delay(Timeout.Infinite, ct));
            var running = await WaitForStateAsync(provider, interrupted, TaskState.Running);
            await Assert.That(running).IsNotNull();

            // 第二个任务先跑完，重启后应当原样保留为「成功」。
            var finished = await first.CreateAsync(TaskType.Copy, (progress, ct) =>
            {
                progress.Report(new TaskProgress { Current = 3, Total = 3 });
                return Task.CompletedTask;
            });
            await WaitForStateAsync(provider, finished, TaskState.Succeeded);

            // 第二轮进程：读回历史。
            var restarted = new TaskService(scopes: scopes);
            await restarted.RestoreAsync();

            var done = await restarted.DoneAsync(TaskType.Copy);
            await Assert.That(done.Code).IsEqualTo(200);

            var interruptedInfo = done.Data!.Single(t => t.Id == interrupted);
            await Assert.That(interruptedInfo.State).IsEqualTo(TaskState.Errored);
            await Assert.That(interruptedInfo.Error).Contains("interrupted by server restart");

            var finishedInfo = done.Data!.Single(t => t.Id == finished);
            await Assert.That(finishedInfo.State).IsEqualTo(TaskState.Succeeded);
            await Assert.That(finishedInfo.Progress).IsEqualTo(3);

            // 未完成任务已在恢复时收尾，不会出现在 undone 列表里。
            var undone = await restarted.UndoneAsync(TaskType.Copy);
            await Assert.That(undone.Data!).Count().IsEqualTo(0);

            // 序号接续：历史里是 copy-1 / copy-2，新任务必须是 copy-3。
            var next = await restarted.CreateAsync(TaskType.Copy, (progress, ct) => Task.CompletedTask);
            await Assert.That(next).IsEqualTo("copy-3");
            await WaitForStateAsync(provider, next, TaskState.Succeeded);

            // 清理第一轮仍在等令牌的任务（删除会取消并移除持久化行）。
            var deleted = await first.DeleteAsync(TaskType.Copy, interrupted);
            await Assert.That(deleted.Code).IsEqualTo(200);
            await Assert.That(await ReadRowAsync(provider, interrupted)).IsNull();
        }
        finally
        {
            await provider.DisposeAsync();
            DeleteDb(path);
        }
    }

    [Test]
    public async Task DeleteAsync_Removes_Persisted_Row()
    {
        var path = NewDbPath();
        var provider = NewProvider(path);
        try
        {
            await EnsureSchemaAsync(provider);
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var svc = new TaskService(scopes: scopes);

            var tid = await svc.CreateAsync(TaskType.Upload, (progress, ct) =>
                Task.Delay(Timeout.Infinite, ct));
            await Assert.That(await WaitForStateAsync(provider, tid, TaskState.Running)).IsNotNull();

            var deleted = await svc.DeleteAsync(TaskType.Upload, tid);
            await Assert.That(deleted.Code).IsEqualTo(200);
            await Assert.That(await ReadRowAsync(provider, tid)).IsNull();
        }
        finally
        {
            await provider.DisposeAsync();
            DeleteDb(path);
        }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"openlist-task-{Guid.NewGuid():N}.db");

    /// <summary>构造一个带 <c>OpenListDbContext</c> 的容器，模拟 Program.cs 的作用域注册。</summary>
    private static ServiceProvider NewProvider(string path)
    {
        var services = new ServiceCollection();
        // OpenListDbContext 的构造函数需要 DatabaseConfig（与 Program.cs 的注册方式一致）。
        services.AddSingleton(new DatabaseConfig());
        services.AddDbContext<OpenListDbContext>(o => o.UseSqlite($"Data Source={path}"));
        return services.BuildServiceProvider();
    }

    private static async Task EnsureSchemaAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OpenListDbContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<BackgroundTask?> ReadRowAsync(ServiceProvider provider, string tid)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        return await db.BackgroundTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid);
    }

    /// <summary>轮询直到持久化行进入目标状态；超时返回 null（避免用固定 sleep 造成抖动）。</summary>
    private static async Task<BackgroundTask?> WaitForStateAsync(ServiceProvider provider, string tid, TaskState expected)
    {
        BackgroundTask? row = null;
        for (var i = 0; i < 100; i++)
        {
            row = await ReadRowAsync(provider, tid);
            if (row?.State == expected) return row;
            await Task.Delay(20);
        }
        return row?.State == expected ? row : null;
    }

    private static void DeleteDb(string path)
    {
        // 连接池会持有文件句柄；清池后才能删掉（删不掉也不影响测试结果）。
        SqliteConnection.ClearAllPools();
        try { File.Delete(path); } catch (IOException) { }
    }
}
