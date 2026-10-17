using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Vfs;

namespace OpenList.Tests;

/// <summary>
/// <see cref="FileSystemService"/> 的批量 move/copy 任务化测试。
/// </summary>
public class MoveCopyBatchTests
{
    private static readonly User MoveUser = new() { Id = 1, Username = "u", Role = UserRole.General, Permission = 0xFF };
    private static readonly User NoPermUser = new() { Id = 2, Username = "n", Role = UserRole.General, Permission = 0 };

    private static (FileSystemService svc, ITaskService tasks, MemoryFileSystem mem) Setup()
    {
        var mount = new MountService();
        var mem = new MemoryFileSystem();
        mount.Root.Mount("/test", mem);
        var tasks = new TaskService();
        var svc = new FileSystemService(mount, signs: null, offline: null, hooks: null,
            scopes: null, tasks: tasks);
        return (svc, tasks, mem);
    }

    [Test]
    public async Task MoveBatchAsync_Returns_Task_AndMovesFiles()
    {
        var (svc, tasks, mem) = Setup();
        mem.CreateDirectory("/dest");
        WriteFile(mem, "/a.txt", "x"u8.ToArray());
        WriteFile(mem, "/b.txt", "y"u8.ToArray());

        var r = await svc.MoveBatchAsync(new MoveCopyReq
        {
            SrcDir = "/test",
            DstDir = "/test/dest",
            Names = ["a.txt", "b.txt"],
        }, MoveUser);

        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Message).Contains("2");
        await Assert.That(r.Data!.Tasks.Count).IsEqualTo(2);
        await WaitForTasks(tasks, TaskType.Move);
        await Assert.That(mem.FileExists("/dest/a.txt")).IsTrue();
        await Assert.That(mem.FileExists("/dest/b.txt")).IsTrue();
        await Assert.That(mem.FileExists("/a.txt")).IsFalse();
    }

    [Test]
    public async Task MoveBatchAsync_Rejects_EmptyNames()
    {
        var (svc, _, mem) = Setup();
        mem.CreateDirectory("/dest");
        var r = await svc.MoveBatchAsync(new MoveCopyReq { SrcDir = "/test", DstDir = "/test/dest" }, MoveUser);
        await Assert.That(r.Code).IsEqualTo(400);
    }

    [Test]
    public async Task MoveBatchAsync_Rejects_NonWritableUser()
    {
        var (svc, _, mem) = Setup();
        mem.CreateDirectory("/dest");
        WriteFile(mem, "/a.txt", "x"u8.ToArray());
        var r = await svc.MoveBatchAsync(new MoveCopyReq
        {
            SrcDir = "/test",
            DstDir = "/test/dest",
            Names = ["a.txt"],
        }, NoPermUser);
        await Assert.That(r.Code).IsEqualTo(403);
    }

    [Test]
    public async Task MoveBatchAsync_SkipExisting_LeavesExistingInPlace()
    {
        var (svc, _, mem) = Setup();
        mem.CreateDirectory("/dest");
        WriteFile(mem, "/a.txt", "x"u8.ToArray());
        WriteFile(mem, "/dest/a.txt", "PRE"u8.ToArray());

        var r = await svc.MoveBatchAsync(new MoveCopyReq
        {
            SrcDir = "/test",
            DstDir = "/test/dest",
            Names = ["a.txt"],
            SkipExisting = true,
        }, MoveUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Message).Contains("completed immediately");
    }

    [Test]
    public async Task CopyBatchAsync_Merge_MergesDirectoryContents()
    {
        var (svc, _, mem) = Setup();
        mem.CreateDirectory("/src/a");
        WriteFile(mem, "/src/a/in.txt", "1"u8.ToArray());
        mem.CreateDirectory("/dest/a");
        WriteFile(mem, "/dest/a/existing.txt", "E"u8.ToArray());
        WriteFile(mem, "/dest/a/in.txt", "OLD"u8.ToArray());

        var r = await svc.CopyBatchAsync(new MoveCopyReq
        {
            SrcDir = "/test/src",
            DstDir = "/test/dest",
            Names = ["a"],
            Merge = true,
        }, MoveUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mem.FileExists("/src/a/in.txt")).IsTrue();
        await Assert.That(mem.FileExists("/dest/a/existing.txt")).IsTrue();
        await Task.Delay(100);
        using var s = mem.OpenFile("/dest/a/in.txt", FileMode.Open, FileAccess.Read);
        var buf = new byte[1];
        s.ReadExactly(buf, 0, 1);
        await Assert.That(buf[0]).IsEqualTo((byte)'1');
    }

    [Test]
    public async Task MoveBatchAsync_Rejects_RelativePathNames()
    {
        // 镜像 Go checkRelativePath：names 只允许纯名称；含 "/" 或 "\" 的路径成分（含反斜杠穿越）
        // 一律 403，否则 UPath 归一化后可越出 src_dir。
        var (svc, _, mem) = Setup();
        mem.CreateDirectory("/dest");
        WriteFile(mem, "/secret.txt", "s"u8.ToArray());

        foreach (var name in new[] { "../../secret.txt", @"..\..\secret.txt", "a/b.txt", ".", "..", "" })
        {
            var r = await svc.MoveBatchAsync(new MoveCopyReq
            {
                SrcDir = "/test",
                DstDir = "/test/dest",
                Names = [name],
            }, MoveUser);
            await Assert.That(r.Code).IsEqualTo(403);
        }

        // 兄弟文件未被移走。
        await Assert.That(mem.FileExists("/secret.txt")).IsTrue();
    }

    [Test]
    public async Task CopyBatchAsync_Rejects_RelativePathNames()
    {
        var (svc, _, mem) = Setup();
        mem.CreateDirectory("/dest");
        WriteFile(mem, "/secret.txt", "s"u8.ToArray());

        foreach (var name in new[] { "../../secret.txt", @"..\..\secret.txt", "a/b.txt" })
        {
            var r = await svc.CopyBatchAsync(new MoveCopyReq
            {
                SrcDir = "/test",
                DstDir = "/test/dest",
                Names = [name],
            }, MoveUser);
            await Assert.That(r.Code).IsEqualTo(403);
        }
        await Assert.That(mem.FileExists("/secret.txt")).IsTrue();
    }

    private static void WriteFile(MemoryFileSystem mem, string path, byte[] bytes)
    {
        using var s = mem.OpenFile(new UPath(path), FileMode.Create, FileAccess.Write);
        s.Write(bytes, 0, bytes.Length);
    }

    private static async Task WaitForTasks(ITaskService tasks, TaskType type)
    {
        for (int i = 0; i < 50; i++)
        {
            var undone = (await tasks.UndoneAsync(type)).Data?.Count ?? 0;
            if (undone == 0) return;
            await Task.Delay(20);
        }
    }
}
