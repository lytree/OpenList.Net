using Microsoft.Extensions.DependencyInjection;
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
/// <item>meta 的 <c>r_sub</c>/<c>header_sub</c>/<c>w_sub</c> 决定对应规则是否传导到子目录。</item>
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

    /// <summary>带 meta 的装配：把固定 meta 列表塞进 DI，供 ListAsync 解析 nearest meta。</summary>
    private static (FileSystemService svc, MemoryFileSystem mem) SetupWithMeta(params Meta[] metas)
    {
        var mount = new MountService();
        var mem = new MemoryFileSystem();
        mount.Root.Mount("/test", mem);
        var services = new ServiceCollection();
        services.AddSingleton<IMetaService>(new FakeMetaService(metas));
        var provider = services.BuildServiceProvider();
        var svc = new FileSystemService(
            mount, null, null, null, provider.GetRequiredService<IServiceScopeFactory>(), null);
        return (svc, mem);
    }

    // ---- meta 的子目录开关 ----

    [Test]
    public async Task ListAsync_Readme_SubOff_OnlyAppliesToMetaPathItself()
    {
        var (svc, mem) = SetupWithMeta(new Meta { Path = "/test/movies", Readme = "简介", RSub = false });
        mem.CreateDirectory("/movies");
        mem.CreateDirectory("/movies/sub");

        var self = await svc.ListAsync(new FsListReq { Path = "/test/movies" }, AdminUser);
        var child = await svc.ListAsync(new FsListReq { Path = "/test/movies/sub" }, AdminUser);

        // r_sub=false：只有 meta 自身那层拿到 readme，子目录不再继承
        await Assert.That(self.Data!.Readme).IsEqualTo("简介");
        await Assert.That(child.Data!.Readme).IsNull();
    }

    [Test]
    public async Task ListAsync_Readme_SubOn_InheritedByChildren()
    {
        var (svc, mem) = SetupWithMeta(new Meta { Path = "/test/movies", Readme = "简介", RSub = true });
        mem.CreateDirectory("/movies");
        mem.CreateDirectory("/movies/sub");

        var child = await svc.ListAsync(new FsListReq { Path = "/test/movies/sub" }, AdminUser);

        await Assert.That(child.Data!.Readme).IsEqualTo("简介");
    }

    [Test]
    public async Task ListAsync_Header_SubOff_OnlyAppliesToMetaPathItself()
    {
        var (svc, mem) = SetupWithMeta(new Meta { Path = "/test/movies", Header = "<b>hi</b>", HeaderSub = false });
        mem.CreateDirectory("/movies");
        mem.CreateDirectory("/movies/sub");

        var self = await svc.ListAsync(new FsListReq { Path = "/test/movies" }, AdminUser);
        var child = await svc.ListAsync(new FsListReq { Path = "/test/movies/sub" }, AdminUser);

        await Assert.That(self.Data!.Header).IsEqualTo("<b>hi</b>");
        await Assert.That(child.Data!.Header).IsNull();
    }

    [Test]
    public async Task ListAsync_WriteFlag_RespectsWSub()
    {
        // MemoryFileSystem 解析不到驱动（driverWritable=false），write 完全由 meta 决定。
        var (svc, mem) = SetupWithMeta(new Meta { Path = "/test/movies", Write = true, WSub = false });
        mem.CreateDirectory("/movies");
        mem.CreateDirectory("/movies/sub");

        var self = await svc.ListAsync(new FsListReq { Path = "/test/movies" }, GuestUser);
        var child = await svc.ListAsync(new FsListReq { Path = "/test/movies/sub" }, GuestUser);

        // w_sub=false：meta 位于自身时开放写入，子目录则不继承
        await Assert.That(self.Data!.Write).IsTrue();
        await Assert.That(child.Data!.Write).IsFalse();
        await Assert.That(child.Data!.WriteContentBypass).IsFalse();
    }

    [Test]
    public async Task ListAsync_WriteFlag_WSubOn_InheritedByChildren()
    {
        var (svc, mem) = SetupWithMeta(new Meta { Path = "/test/movies", Write = true, WSub = true });
        mem.CreateDirectory("/movies");
        mem.CreateDirectory("/movies/sub");

        var child = await svc.ListAsync(new FsListReq { Path = "/test/movies/sub" }, GuestUser);

        await Assert.That(child.Data!.Write).IsTrue();
        await Assert.That(child.Data!.WriteContentBypass).IsTrue();
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

    /// <summary>只实现 <see cref="IMetaService.ListAsync"/> 的桩：ListAsync 之外的成员不应被调用。</summary>
    private sealed class FakeMetaService(IReadOnlyList<Meta> metas) : IMetaService
    {
        public Task<Resp<List<Meta>>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(Resp<List<Meta>>.Ok(metas.ToList()));

        public Task<Resp<Meta>> GetAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Resp<Meta>> GetByIdAsync(long id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Resp<Meta>> CreateAsync(Meta meta, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Resp> UpdateAsync(long id, Meta meta, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Resp> DeleteAsync(long id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
