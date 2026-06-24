using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using Zio;
using Zio.FileSystems;

namespace OpenList.Tests;

/// <summary>
/// FileSystemService tests against an in-memory MemoryFileSystem mounted into ZioMountService.
/// Covers: list, get, mkdir, rename, move, copy, remove, put, dirs, search.
/// </summary>
public class FileSystemServiceTests
{
    private static (ZioMountService mount, FileSystemService svc, MemoryFileSystem mem) Setup()
    {
        var mount = new ZioMountService();
        var mem = new MemoryFileSystem();
        // Mount memory fs at /test — bypasses driver loading for unit tests.
        mount.Root.Mount("/test", mem);
        var svc = new FileSystemService(mount);
        return (mount, svc, mem);
    }

    private static readonly User TestUser = new() { Id = 1, Username = "test", Role = UserRole.Admin };

    /// <summary>Helper: write a file with the given bytes via the mount root (handles mount point translation).</summary>
    private static async Task WriteFileAsync(ZioMountService mount, string path, byte[] bytes)
    {
        // Ensure parent directories exist
        var upath = new UPath(path);
        var parent = upath.GetDirectory();
        if (!mount.Root.DirectoryExists(parent))
            mount.Root.CreateDirectory(parent);
        await using var s = mount.Root.OpenFile(upath, FileMode.Create, FileAccess.Write);
        await s.WriteAsync(bytes);
    }

    private static async Task WriteFileAsync(ZioMountService mount, string path, string text)
        => await WriteFileAsync(mount, path, System.Text.Encoding.UTF8.GetBytes(text));

    [Test]
    public async Task ListAsync_Returns_Directories_And_Files()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/sub1");
        mount.Root.CreateDirectory("/test/sub2");
        await WriteFileAsync(mount, "/test/a.txt", new byte[] { 65 });
        await WriteFileAsync(mount, "/test/b.txt", new byte[] { 66 });

        var r = await svc.ListAsync(new FsListReq { Path = "/test" }, TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Total).IsEqualTo(4);
        // Directories first (IsDir=true sorted before false)
        await Assert.That(r.Data.Content[0].IsDir).IsTrue();
        await Assert.That(r.Data.Content[2].IsDir).IsFalse();
    }

    [Test]
    public async Task ListAsync_Paginates_Results()
    {
        var (mount, svc, _) = Setup();
        for (int i = 0; i < 5; i++)
            await WriteFileAsync(mount, $"/test/f{i}.txt", new byte[] { (byte)i });

        var r = await svc.ListAsync(new FsListReq { Path = "/test", Page = 1, PerPage = 2 }, TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Total).IsEqualTo(5);
        await Assert.That(r.Data.Content).HasCount().EqualTo(2);

        var r2 = await svc.ListAsync(new FsListReq { Path = "/test", Page = 3, PerPage = 2 }, TestUser);
        await Assert.That(r2.Data!.Content).HasCount().EqualTo(1);
    }

    [Test]
    public async Task ListAsync_Returns_404_For_Nonexistent_Path()
    {
        var (_, svc, _) = Setup();
        var r = await svc.ListAsync(new FsListReq { Path = "/test/nope" }, TestUser);
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task GetAsync_Returns_File_Metadata()
    {
        var (mount, svc, _) = Setup();
        await WriteFileAsync(mount, "/test/hello.txt", "hello world");

        var r = await svc.GetAsync(new FsGetReq { Path = "/test/hello.txt" }, TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.IsDir).IsFalse();
        await Assert.That(r.Data.Name).IsEqualTo("hello.txt");
        await Assert.That(r.Data.Size).IsEqualTo(11);
    }

    [Test]
    public async Task GetAsync_Returns_Directory_Metadata()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/mydir");

        var r = await svc.GetAsync(new FsGetReq { Path = "/test/mydir" }, TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.IsDir).IsTrue();
    }

    [Test]
    public async Task MkdirAsync_Creates_Directory()
    {
        var (_, svc, _) = Setup();
        var r = await svc.MkdirAsync("/test/newdir", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        var get = await svc.GetAsync(new FsGetReq { Path = "/test/newdir" }, TestUser);
        await Assert.That(get.Data!.IsDir).IsTrue();
    }

    [Test]
    public async Task MkdirAsync_Rejects_Existing_Path()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/exists");
        var r = await svc.MkdirAsync("/test/exists", TestUser);
        await Assert.That(r.Code).IsEqualTo(409);
    }

    [Test]
    public async Task RenameAsync_Moves_File()
    {
        var (mount, svc, _) = Setup();
        await WriteFileAsync(mount, "/test/old.txt", new byte[] { 1 });

        var r = await svc.RenameAsync("/test/old.txt", "/test/new.txt", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.FileExists("/test/old.txt")).IsFalse();
        await Assert.That(mount.Root.FileExists("/test/new.txt")).IsTrue();
    }

    [Test]
    public async Task RenameAsync_Moves_Directory()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/olddir");
        await WriteFileAsync(mount, "/test/olddir/inner.txt", new byte[] { 1 });

        var r = await svc.RenameAsync("/test/olddir", "/test/newdir", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.DirectoryExists("/test/olddir")).IsFalse();
        await Assert.That(mount.Root.FileExists("/test/newdir/inner.txt")).IsTrue();
    }

    [Test]
    public async Task MoveAsync_Moves_To_Destination_Dir()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/dest");
        await WriteFileAsync(mount, "/test/file.txt", new byte[] { 1 });

        var r = await svc.MoveAsync(["/test/file.txt"], "/test/dest", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.FileExists("/test/file.txt")).IsFalse();
        await Assert.That(mount.Root.FileExists("/test/dest/file.txt")).IsTrue();
    }

    [Test]
    public async Task CopyAsync_Copies_File()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/dest");
        await WriteFileAsync(mount, "/test/src.txt", "data");

        var r = await svc.CopyAsync(["/test/src.txt"], "/test/dest", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.FileExists("/test/src.txt")).IsTrue();
        await Assert.That(mount.Root.FileExists("/test/dest/src.txt")).IsTrue();
    }

    [Test]
    public async Task CopyAsync_Copies_Directory_Recursively()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/srcdir");
        mount.Root.CreateDirectory("/test/dest");
        await WriteFileAsync(mount, "/test/srcdir/a.txt", new byte[] { 1 });
        await WriteFileAsync(mount, "/test/srcdir/b.txt", new byte[] { 2 });

        var r = await svc.CopyAsync(["/test/srcdir"], "/test/dest", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.FileExists("/test/dest/srcdir/a.txt")).IsTrue();
        await Assert.That(mount.Root.FileExists("/test/dest/srcdir/b.txt")).IsTrue();
        // Source remains
        await Assert.That(mount.Root.FileExists("/test/srcdir/a.txt")).IsTrue();
    }

    [Test]
    public async Task RemoveAsync_Deletes_Files_And_Directories()
    {
        var (mount, svc, _) = Setup();
        await WriteFileAsync(mount, "/test/file.txt", new byte[] { 1 });
        mount.Root.CreateDirectory("/test/dir");
        await WriteFileAsync(mount, "/test/dir/inner.txt", new byte[] { 2 });

        var r = await svc.RemoveAsync(["/test/file.txt", "/test/dir"], TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.FileExists("/test/file.txt")).IsFalse();
        await Assert.That(mount.Root.DirectoryExists("/test/dir")).IsFalse();
    }

    [Test]
    public async Task PutStreamAsync_Writes_File()
    {
        var (mount, svc, _) = Setup();
        using var stream = new MemoryStream("hello"u8.ToArray());
        var r = await svc.PutStreamAsync("/test/uploaded.txt", stream, TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.FileExists("/test/uploaded.txt")).IsTrue();
        using var rs = mount.Root.OpenFile("/test/uploaded.txt", FileMode.Open, FileAccess.Read);
        using var reader = new StreamReader(rs);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("hello");
    }

    [Test]
    public async Task PutStreamAsync_Creates_Parent_Directories()
    {
        var (mount, svc, _) = Setup();
        using var stream = new MemoryStream("x"u8.ToArray());
        var r = await svc.PutStreamAsync("/test/a/b/c.txt", stream, TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.FileExists("/test/a/b/c.txt")).IsTrue();
    }

    [Test]
    public async Task DirsAsync_Returns_Subdirectories()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/d1");
        mount.Root.CreateDirectory("/test/d2");
        mount.Root.CreateDirectory("/test/d1/sub");

        var r = await svc.DirsAsync("/test", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Dirs).Contains("/test/d1");
        await Assert.That(r.Data.Dirs).Contains("/test/d2");
        await Assert.That(r.Data.Dirs).Contains("/test/d1/sub");
    }

    [Test]
    public async Task SearchAsync_Finds_Matching_Files()
    {
        var (mount, svc, _) = Setup();
        await WriteFileAsync(mount, "/test/report.pdf", new byte[] { 1 });
        await WriteFileAsync(mount, "/test/notes.txt", new byte[] { 2 });
        mount.Root.CreateDirectory("/test/reports");

        var r = await svc.SearchAsync(new SearchReq { Parent = "/test", Keywords = "report" }, TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Total).IsEqualTo(2); // report.pdf + reports/
        await Assert.That(r.Data.Content.Any(o => o.Name == "report.pdf")).IsTrue();
        await Assert.That(r.Data.Content.Any(o => o.Name == "reports")).IsTrue();
    }

    [Test]
    public async Task SearchAsync_NonRecursive_Only_Searches_Current_Level()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/sub");
        await WriteFileAsync(mount, "/test/sub/target.txt", new byte[] { 1 });

        var r = await svc.SearchAsync(new SearchReq { Parent = "/test", Keywords = "target", Recursive = false }, TestUser);
        await Assert.That(r.Data!.Total).IsEqualTo(0);

        var r2 = await svc.SearchAsync(new SearchReq { Parent = "/test", Keywords = "target", Recursive = true }, TestUser);
        await Assert.That(r2.Data!.Total).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveEmptyDirectoryAsync_Removes_Empty_Dir()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/empty");
        var r = await svc.RemoveEmptyDirectoryAsync("/test/empty", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(mount.Root.DirectoryExists("/test/empty")).IsFalse();
    }

    [Test]
    public async Task RemoveEmptyDirectoryAsync_Rejects_NonEmpty_Dir()
    {
        var (mount, svc, _) = Setup();
        mount.Root.CreateDirectory("/test/notempty");
        await WriteFileAsync(mount, "/test/notempty/file.txt", new byte[] { 1 });

        var r = await svc.RemoveEmptyDirectoryAsync("/test/notempty", TestUser);
        await Assert.That(r.Code).IsEqualTo(400);
    }

    [Test]
    public async Task ArchiveMetaAsync_Returns_Supported_Extensions()
    {
        var (_, svc, _) = Setup();
        var r = await svc.ArchiveMetaAsync("/", TestUser);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.SupportedExtensions).Contains(".zip");
    }

    [Test]
    public async Task LinkAsync_Returns_501_For_Local_Driver()
    {
        var (_, svc, _) = Setup();
        var r = await svc.LinkAsync("/test/file.txt", TestUser);
        await Assert.That(r.Code).IsEqualTo(501);
    }
}
