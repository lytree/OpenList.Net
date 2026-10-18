using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Vfs;

namespace OpenList.Tests;

/// <summary>
/// 虚拟目录（根目录与挂载点祖先）的回归测试。
/// <para>
/// 背景：<see cref="MountFileSystem"/> 只在各存储的挂载路径上挂子文件系统，而基类
/// <see cref="FileSystem"/> 把「根目录恒存在」写死在 <c>DirectoryExists</c> /
/// <c>ValidatePath</c> 里。两边不一致时，<c>POST /api/fs/get</c> 传 <c>path=/</c> 会先通过
/// 存在性校验，再在 <c>GetLastWriteTime("/")</c> 抛 <see cref="FileNotFoundException"/>。
/// </para>
/// </summary>
public class MountFileSystemVirtualDirTests
{
    private static readonly User TestUser = new() { Id = 1, Username = "test", Role = UserRole.Admin };

    [Test]
    public async Task Root_Has_Metadata_Even_When_No_Mount_At_Root()
    {
        var mount = new MountService();
        mount.Root.Mount("/test", new MemoryFileSystem());

        var entry = mount.Root.GetDirectoryEntry(UPath.Root);

        await Assert.That(entry.IsDirectory).IsTrue();
        await Assert.That(entry.LastWriteTime).IsEqualTo(MountFileSystem.VirtualDirectoryTime);
        await Assert.That(mount.Root.GetAttributes(UPath.Root)).IsEqualTo(FileAttributes.Directory);
    }

    [Test]
    public async Task GetAsync_On_Root_Returns_Directory_Instead_Of_Throwing()
    {
        var mount = new MountService();
        mount.Root.Mount("/test", new MemoryFileSystem());
        var svc = new FileSystemService(mount);

        var r = await svc.GetAsync(new FsGetReq { Path = "/" }, TestUser);

        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.IsDir).IsTrue();
        await Assert.That(r.Data.Name).IsEqualTo("/");
    }

    [Test]
    public async Task GetAsync_On_Root_Works_With_No_Storage_At_All()
    {
        var svc = new FileSystemService(new MountService());

        var get = await svc.GetAsync(new FsGetReq { Path = "/" }, TestUser);
        var list = await svc.ListAsync(new FsListReq { Path = "/" }, TestUser);

        await Assert.That(get.Code).IsEqualTo(200);
        await Assert.That(get.Data!.IsDir).IsTrue();
        await Assert.That(list.Code).IsEqualTo(200);
        await Assert.That(list.Data!.Total).IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_On_Root_Shows_Mount_Points()
    {
        var mount = new MountService();
        mount.Root.Mount("/test", new MemoryFileSystem());
        mount.Root.Mount("/other", new MemoryFileSystem());
        var svc = new FileSystemService(mount);

        var r = await svc.ListAsync(new FsListReq { Path = "/" }, TestUser);

        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Total).IsEqualTo(2);
        await Assert.That(r.Data.Content.Select(o => o.Name).OrderBy(n => n).ToList())
            .IsEquivalentTo(new List<string> { "other", "test" });
        await Assert.That(r.Data.Content.All(o => o.IsDir)).IsTrue();
    }

    [Test]
    public async Task Nested_Mount_Parent_Is_A_Virtual_Directory()
    {
        var mount = new MountService();
        mount.Root.Mount("/a/b", new MemoryFileSystem());

        // /a 没有任何存储，但它是 /a/b 的祖先，必须可进入。
        await Assert.That(mount.Root.DirectoryExists("/a")).IsTrue();
        await Assert.That(mount.Root.IsVirtualDirectory("/a")).IsTrue();
        await Assert.That(mount.Root.EnumeratePaths("/").Select(p => p.ToString()).ToList())
            .IsEquivalentTo(new List<string> { "/a" });
        await Assert.That(mount.Root.EnumeratePaths("/a").Select(p => p.ToString()).ToList())
            .IsEquivalentTo(new List<string> { "/a/b" });

        var svc = new FileSystemService(mount);
        var get = await svc.GetAsync(new FsGetReq { Path = "/a" }, TestUser);
        await Assert.That(get.Code).IsEqualTo(200);
        await Assert.That(get.Data!.IsDir).IsTrue();
        await Assert.That(get.Data.Name).IsEqualTo("a");

        var list = await svc.ListAsync(new FsListReq { Path = "/" }, TestUser);
        await Assert.That(list.Data!.Content[0].Name).IsEqualTo("a");
    }

    [Test]
    public async Task Mount_Under_A_Mounted_Directory_Is_Also_Listed()
    {
        var mount = new MountService();
        var parentFs = new MemoryFileSystem();
        parentFs.WriteAllBytes("/own.txt", [1]);
        mount.Root.Mount("/a", parentFs);
        mount.Root.Mount("/a/b", new MemoryFileSystem());

        var names = mount.Root.EnumeratePaths("/a").Select(p => p.GetName()).OrderBy(n => n).ToList();

        // /a 自己的内容 + 子挂载点 b 都要出现，且不重复。
        await Assert.That(names).IsEquivalentTo(new List<string> { "b", "own.txt" });
    }

    [Test]
    public async Task Virtual_Directory_Rejects_Deletion()
    {
        var mount = new MountService();
        mount.Root.Mount("/a/b", new MemoryFileSystem());

        await Assert.That(() => mount.Root.DeleteDirectory("/a", true))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task NonExistent_Path_Still_Reports_Missing()
    {
        var mount = new MountService();
        mount.Root.Mount("/a/b", new MemoryFileSystem());
        var svc = new FileSystemService(mount);

        var r = await svc.GetAsync(new FsGetReq { Path = "/nope" }, TestUser);
        var list = await svc.ListAsync(new FsListReq { Path = "/nope" }, TestUser);

        await Assert.That(r.Code).IsEqualTo(RespCodes.NotFound);
        await Assert.That(list.Code).IsEqualTo(RespCodes.NotFound);
    }
}
