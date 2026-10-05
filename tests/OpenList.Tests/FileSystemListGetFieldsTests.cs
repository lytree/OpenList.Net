using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Vfs;

namespace OpenList.Tests;

/// <summary>
/// 验证 <see cref="FileSystemService"/> 在 List / Get 中补齐的字段：
/// <list type="bullet">
/// <item><c>refresh=true</c> 非管理员返回 403；</item>
/// <item><c>list</c> 返回 <c>provider</c> 与 <c>write</c>（驱动 ReadOnly 联动）；</item>
/// <item><c>get</c> 对视频文件附带同目录同名（去扩展名）前缀的兄弟；非视频文件不附带。</item>
/// </list>
/// </summary>
public class FileSystemListGetFieldsTests
{
    private static readonly User AdminUser = new() { Id = 1, Username = "admin", Role = UserRole.Admin, Permission = 0x7F };
    private static readonly User GuestUser = new() { Id = 2, Username = "guest", Role = UserRole.Guest };

    private static (FileSystemService svc, MemoryFileSystem mem) Setup()
    {
        var mount = new MountService();
        var mem = new MemoryFileSystem();
        mount.Root.Mount("/test", mem);
        return (new FileSystemService(mount), mem);
    }

    [Test]
    public async Task ListAsync_Refresh_RequiresAdmin()
    {
        var (svc, _) = Setup();
        var r = await svc.ListAsync(new FsListReq { Path = "/test", Refresh = true }, GuestUser);
        await Assert.That(r.Code).IsEqualTo(403);
    }

    [Test]
    public async Task ListAsync_WithoutScopes_DoesNot_Crash()
    {
        var (svc, _) = Setup();
        var r = await svc.ListAsync(new FsListReq { Path = "/test" }, AdminUser);
        await Assert.That(r.Code).IsEqualTo(200);
        // /test 挂的是 MemoryFileSystem，driver 解析不到，Provider 应为 null。
        await Assert.That(r.Data!.Provider).IsNull();
        // 没有 IFileSystemDirectUploader，DirectUploadTools 应为 null。
        await Assert.That(r.Data!.DirectUploadTools).IsNull();
    }

    [Test]
    public async Task GetAsync_Related_OnlyForVideoFiles()
    {
        var (svc, mem) = Setup();
        // Memory fs 内部路径相对挂载点 /test —— 直接 /movies。
        mem.CreateDirectory("/movies");
        WriteFile(mem, "/movies/movie_intro.mp4", new byte[] { 1 });
        WriteFile(mem, "/movies/movie_intro_part2.mp4", new byte[] { 2 });
        WriteFile(mem, "/movies/movie_intro.srt", new byte[] { 3 });
        WriteFile(mem, "/movies/unrelated.mp4", new byte[] { 4 });

        var r = await svc.GetAsync(new FsGetReq { Path = "/test/movies/movie_intro.mp4" }, AdminUser);
        await Assert.That(r.Code).IsEqualTo(200);
        // Related：去扩展名前缀 movie_intro → 同目录里以 movie_intro 开头的兄弟（不含自身）。
        await Assert.That(r.Data!.Related.Count).IsEqualTo(2);
        var names = r.Data.Related.Select(o => o.Name).OrderBy(n => n).ToList();
        await Assert.That(names).IsEquivalentTo(new[] { "movie_intro.srt", "movie_intro_part2.mp4" });
    }

    [Test]
    public async Task GetAsync_Related_Empty_ForNonVideo()
    {
        var (svc, mem) = Setup();
        mem.CreateDirectory("/docs");
        WriteFile(mem, "/docs/report.pdf", new byte[] { 1 });
        WriteFile(mem, "/docs/report_old.pdf", new byte[] { 2 });

        var r = await svc.GetAsync(new FsGetReq { Path = "/test/docs/report.pdf" }, AdminUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Related).IsEmpty();
    }

    private static void WriteFile(MemoryFileSystem mem, string path, byte[] bytes)
    {
        using var s = mem.OpenFile(new UPath(path), FileMode.Create, FileAccess.Write);
        s.Write(bytes, 0, bytes.Length);
    }
}
