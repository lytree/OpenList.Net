using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;
using OpenList.Media.Domain;
using OpenList.Media.Services;
using OpenList.Vfs;

namespace OpenList.Tests;

/// <summary>
/// 媒体清理服务（<c>IMediaCleanupService</c>）的服务层用例。
/// <para>
/// 文件系统用 <see cref="MemoryFileSystem"/> 挂在 <c>/test</c>：用例要断言「文件被移进回收站 /
/// 被删掉 / 真的还在」，只有真实实现才测得出来；内存实现比临时目录快且不会留垃圾。
/// </para>
/// <para>
/// 注意挂载后的路径换算：VFS 上是 <c>/test/lib/a.mkv</c>，内存文件系统里看到的是 <c>/lib/a.mkv</c>
/// （挂载点被剥掉）。<see cref="MemPath"/> 负责这层换算，避免每个用例各写一遍。
/// </para>
/// </summary>
public class MediaCleanupTests
{
    private static readonly User Admin = new() { Id = 1, Username = "admin" };
    private static readonly User Guest = new() { Id = 0 };
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // 孤儿条目
    // ------------------------------------------------------------------

    [Test]
    public async Task Orphans_Are_Items_Whose_Files_Are_All_Gone()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        var alive = await SeedItemAsync(h.Db, lib.Id, "还在的", "lib/alive", ("a.mkv", 10, 0));
        var gone = await SeedItemAsync(h.Db, lib.Id, "文件没了的", "lib/gone", ("a.mkv", 10, 0), ("b.mkv", 20, 0));
        var empty = await SeedItemAsync(h.Db, lib.Id, "本来就没文件", "lib/empty");
        // 「还在的」必须真的有文件：孤儿判定的依据是 VFS 上的存在性，不是数据库行数。
        h.Write("/test/lib/alive/a.mkv", [1, 2, 3]);

        var resp = await h.Service.ListOrphansAsync(lib.Id, Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        var ids = resp.Data!.Select(o => o.ItemId).ToList();
        await Assert.That(ids).DoesNotContain(alive.Id);
        await Assert.That(ids).Contains(gone.Id);
        await Assert.That(ids).Contains(empty.Id);

        // MissingFiles 记录「这条原来有几个文件行」：两条文件的报 2，本来没有文件的报 0。
        await Assert.That(resp.Data!.First(o => o.ItemId == gone.Id).MissingFiles).IsEqualTo(2);
        await Assert.That(resp.Data!.First(o => o.ItemId == empty.Id).MissingFiles).IsEqualTo(0);
        // 跨库扫描时前端要靠库名标出来源。
        await Assert.That(resp.Data!.First(o => o.ItemId == gone.Id).LibraryName).IsEqualTo(lib.Name);
    }

    [Test]
    public async Task Orphans_Only_Look_At_Visible_Libraries()
    {
        using var h = new Harness();
        var pub = await SeedLibraryAsync(h.Db, name: "公开库");
        var priv = await SeedLibraryAsync(h.Db, path: "/test/priv", name: "私有库", @private: true);
        var pubItem = await SeedItemAsync(h.Db, pub.Id, "公开的", "lib/x");
        var privItem = await SeedItemAsync(h.Db, priv.Id, "私有的", "priv/x");

        // 指定库：不存在与不可见都按 404 处理（不泄露私有库的存在性）。
        var missing = await h.Service.ListOrphansAsync(9999, Admin);
        await Assert.That(missing.Code).IsEqualTo(404);

        var guestOnPrivate = await h.Service.ListOrphansAsync(priv.Id, Guest);
        await Assert.That(guestOnPrivate.Code).IsEqualTo(404);

        var adminOnPrivate = await h.Service.ListOrphansAsync(priv.Id, Admin);
        await Assert.That(adminOnPrivate.Code).IsEqualTo(200);
        await Assert.That(adminOnPrivate.Data!.Select(o => o.ItemId)).Contains(privItem.Id);

        // 不指定库（0 = 全部可见库）：游客看不到私有库的条目。
        var guestAll = await h.Service.ListOrphansAsync(0, Guest);
        await Assert.That(guestAll.Code).IsEqualTo(200);
        await Assert.That(guestAll.Data!.Select(o => o.ItemId)).DoesNotContain(privItem.Id).And.Contains(pubItem.Id);

        var adminAll = await h.Service.ListOrphansAsync(0, Admin);
        await Assert.That(adminAll.Data!.Select(o => o.LibraryId)).Contains(pub.Id).And.Contains(priv.Id);
    }

    [Test]
    public async Task Delete_Orphans_Removes_Rows_But_Never_Touches_Files()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        var item = await SeedItemAsync(h.Db, lib.Id, "孤儿", "lib/orphan", ("a.mkv", 10, 0));
        // 文件行在，但磁盘上还有同名文件（模拟：扫描还没跑到）。孤儿删除按约定只删行。
        h.Write("/test/lib/orphan/a.mkv", [1, 2, 3]);
        var state = new UserMediaState { UserId = Admin.Id, ItemId = item.Id, Favorite = true };
        h.Db.UserMediaStates.Add(state);
        await h.Db.SaveChangesAsync();

        var resp = await h.Service.DeleteOrphansAsync([item.Id], Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(await h.Db.MediaItems.CountAsync()).IsEqualTo(0);
        await Assert.That(await h.Db.MediaFiles.CountAsync()).IsEqualTo(0);
        await Assert.That(await h.Db.UserMediaStates.CountAsync()).IsEqualTo(0);
        // 关键：文件本身还在 —— 孤儿清理的语义是「文件早没了，只剩行」。
        await Assert.That(h.Exists("/test/lib/orphan/a.mkv")).IsTrue();
    }

    [Test]
    public async Task Delete_Orphans_Is_Idempotent_And_Rejects_Guests()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        var item = await SeedItemAsync(h.Db, lib.Id, "孤儿", "lib/orphan");

        await Assert.That((await h.Service.DeleteOrphansAsync([item.Id], Guest)).Code).IsEqualTo(403);

        await Assert.That((await h.Service.DeleteOrphansAsync([item.Id], Admin)).Code).IsEqualTo(200);
        // 第二次：行已经没了，仍然 200（清理页可能重复点）。
        await Assert.That((await h.Service.DeleteOrphansAsync([item.Id], Admin)).Code).IsEqualTo(200);
        // 空列表同样直接成功。
        await Assert.That((await h.Service.DeleteOrphansAsync([], Admin)).Code).IsEqualTo(200);
    }

    [Test]
    public async Task Delete_Orphans_Clears_Related_Rows_And_Want_Backfill()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        var item = await SeedItemAsync(h.Db, lib.Id, "孤儿", "lib/orphan");
        var person = new MediaPerson { Name = "演员", TmdbId = "42" };
        h.Db.MediaPersons.Add(person);
        await h.Db.SaveChangesAsync();

        h.Db.MediaItemPersons.Add(new MediaItemPerson { ItemId = item.Id, PersonId = person.Id, Role = "actor" });
        h.Db.MediaWatchSessions.Add(new MediaWatchSession
        {
            UserId = Admin.Id, ItemId = item.Id, Token = "t1", StartedAt = Now, LastAt = Now,
        });
        var playlist = new MediaPlaylist { UserId = Admin.Id, Name = "我的", CreatedAt = Now, UpdatedAt = Now };
        h.Db.MediaPlaylists.Add(playlist);
        await h.Db.SaveChangesAsync();
        h.Db.MediaPlaylistItems.Add(new MediaPlaylistItem { PlaylistId = playlist.Id, ItemId = item.Id, Position = 0, AddedAt = Now });
        var want = new MediaWant { ExternalId = "550", MediaType = "movie", Title = "孤儿", ItemId = item.Id, CreatedAt = Now };
        h.Db.MediaWants.Add(want);
        await h.Db.SaveChangesAsync();

        await h.Service.DeleteOrphansAsync([item.Id], Admin);

        await Assert.That(await h.Db.MediaItemPersons.CountAsync()).IsEqualTo(0);
        await Assert.That(await h.Db.MediaWatchSessions.CountAsync()).IsEqualTo(0);
        await Assert.That(await h.Db.MediaPlaylistItems.CountAsync()).IsEqualTo(0);
        // 想看记录保留，只把指向已删条目的回填写空，否则会显示一条点进去 404 的「已入库」。
        await Assert.That(await h.Db.MediaWants.CountAsync()).IsEqualTo(1);
        await Assert.That((await h.Db.MediaWants.SingleAsync()).ItemId).IsNull();
    }

    // ------------------------------------------------------------------
    // 重复文件
    // ------------------------------------------------------------------

    [Test]
    public async Task Duplicates_Require_Same_Size_Duration_And_Prefix_Hash()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        // 同一部片子被扫成两个条目，文件内容一致 → 应当成组。
        var a = await SeedItemAsync(h.Db, lib.Id, "甲", "lib/a", ("movie.mkv", 1024, 5400));
        var b = await SeedItemAsync(h.Db, lib.Id, "乙", "lib/b", ("movie.mkv", 1024, 5400));
        h.Write("/test/lib/a/movie.mkv", Filler(1024, 7));
        h.Write("/test/lib/b/movie.mkv", Filler(1024, 7));

        // 同尺寸不同内容 → 哈希不同，不算重复。
        var c = await SeedItemAsync(h.Db, lib.Id, "丙", "lib/c", ("movie.mkv", 1024, 5400));
        h.Write("/test/lib/c/movie.mkv", Filler(1024, 9));

        var resp = await h.Service.ListDuplicatesAsync(lib.Id, Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(resp.Data!.Count).IsEqualTo(1);
        var group = resp.Data![0];
        await Assert.That(group.SizeBytes).IsEqualTo(1024);
        await Assert.That(group.Count).IsEqualTo(2);
        await Assert.That(group.Items.Select(i => i.ItemId).OrderBy(x => x))
            .IsEquivalentTo(new List<long> { a.Id, b.Id });
        await Assert.That(group.Items.Select(i => i.ItemId)).DoesNotContain(c.Id);
    }

    [Test]
    public async Task Duplicates_Do_Not_Group_Files_With_Different_Duration()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        // 内容与大小都一样，但时长不同 —— 一个是正片一个是预告，不能算重复。
        await SeedItemAsync(h.Db, lib.Id, "正片", "lib/a", ("movie.mkv", 512, 5400));
        await SeedItemAsync(h.Db, lib.Id, "预告", "lib/b", ("movie.mkv", 512, 120));
        h.Write("/test/lib/a/movie.mkv", Filler(512, 3));
        h.Write("/test/lib/b/movie.mkv", Filler(512, 3));

        var resp = await h.Service.ListDuplicatesAsync(lib.Id, Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(resp.Data!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Delete_Duplicates_Moves_Files_Into_Trash_And_Removes_Rows()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db, trash: "/test/trash");
        var keep = await SeedItemAsync(h.Db, lib.Id, "保留", "lib/keep", ("m.mkv", 256, 100));
        var dup = await SeedItemAsync(h.Db, lib.Id, "重复", "lib/dup", ("m.mkv", 256, 100));
        h.Write("/test/lib/keep/m.mkv", Filler(256, 1));
        h.Write("/test/lib/dup/m.mkv", Filler(256, 1));

        var resp = await h.Service.DeleteDuplicatesAsync([dup.Id], Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(await h.Db.MediaItems.CountAsync()).IsEqualTo(1);
        await Assert.That((await h.Db.MediaItems.SingleAsync()).Id).IsEqualTo(keep.Id);
        // 配了回收站：文件被移走而不是删掉，并按原路径镜像过去。
        await Assert.That(h.Exists("/test/lib/dup/m.mkv")).IsFalse();
        await Assert.That(h.Exists("/test/trash/test/lib/dup/m.mkv")).IsTrue();
    }

    [Test]
    public async Task Delete_Duplicates_Deletes_Files_When_No_Trash_Is_Configured()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        var dup = await SeedItemAsync(h.Db, lib.Id, "重复", "lib/dup", ("m.mkv", 128, 100));
        h.Write("/test/lib/dup/m.mkv", Filler(128, 2));

        var resp = await h.Service.DeleteDuplicatesAsync([dup.Id], Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(await h.Db.MediaItems.CountAsync()).IsEqualTo(0);
        await Assert.That(h.Exists("/test/lib/dup/m.mkv")).IsFalse();
    }

    // ------------------------------------------------------------------
    // 单条删除 / 回收站
    // ------------------------------------------------------------------

    [Test]
    public async Task Delete_Item_With_DeleteFiles_False_Only_Drops_Rows()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        var item = await SeedItemAsync(h.Db, lib.Id, "条目", "lib/x", ("m.mkv", 32, 10));
        h.Write("/test/lib/x/m.mkv", Filler(32, 4));

        var resp = await h.Service.DeleteItemAsync(item.Id, deleteFiles: false, Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(await h.Db.MediaItems.CountAsync()).IsEqualTo(0);
        await Assert.That(h.Exists("/test/lib/x/m.mkv")).IsTrue();
    }

    [Test]
    public async Task Delete_Item_With_Files_Already_Gone_Still_Removes_Row()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        var item = await SeedItemAsync(h.Db, lib.Id, "条目", "lib/x", ("m.mkv", 32, 10));

        // deleteFiles = true，但文件早没了：不该卡在「删不掉」上。
        var resp = await h.Service.DeleteItemAsync(item.Id, deleteFiles: true, Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(await h.Db.MediaItems.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_Trash_Clears_Contents_And_Keeps_The_Directory()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db, trash: "/test/trash");
        h.Write("/test/trash/old/a.mkv", Filler(8, 1));
        h.Write("/test/trash/old/b.mkv", Filler(8, 2));

        var resp = await h.Service.EmptyTrashAsync(lib.Id, Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(h.Exists("/test/trash/old/a.mkv")).IsFalse();
        await Assert.That(h.Exists("/test/trash/old/b.mkv")).IsFalse();
        await Assert.That(h.Exists("/test/trash/old")).IsFalse();
        // 回收站目录本身保留，后续删除还能直接往里放。
        await Assert.That(h.Exists("/test/trash")).IsTrue();
    }

    [Test]
    public async Task Empty_Trash_Requires_A_Configured_Directory()
    {
        using var h = new Harness();
        var lib = await SeedLibraryAsync(h.Db);
        var resp = await h.Service.EmptyTrashAsync(lib.Id, Admin);
        await Assert.That(resp.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Empty_Trash_Refuses_Storage_Root_And_Unknown_Library()
    {
        using var h = new Harness();
        var root = await SeedLibraryAsync(h.Db, name: "指到根", trash: "/");
        await Assert.That((await h.Service.EmptyTrashAsync(root.Id, Admin)).Code).IsEqualTo(400);
        await Assert.That((await h.Service.EmptyTrashAsync(9999, Admin)).Code).IsEqualTo(404);
    }

    // ------------------------------------------------------------------
    // 帮助方法与替身
    // ------------------------------------------------------------------

    /// <summary>把 VFS 路径换算成内存文件系统里的路径（剥掉挂载点 <c>/test</c>）。</summary>
    private static string MemPath(string vfsPath)
        => vfsPath.StartsWith("/test", StringComparison.Ordinal) ? vfsPath["/test".Length..] : vfsPath;

    /// <summary>生成可复现的定长内容：同一 seed 得同一份字节。</summary>
    private static byte[] Filler(int size, byte seed)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)((seed + i * 31) & 0xFF);
        return bytes;
    }

    private static async Task<MediaLibrary> SeedLibraryAsync(
        OpenListDbContext db, string path = "/test/lib", string name = "电影", bool @private = false, string? trash = null)
    {
        var lib = new MediaLibrary
        {
            Name = name,
            Path = path,
            Type = MediaLibraryType.Movie,
            Private = @private,
            TrashDirectory = trash,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        db.MediaLibraries.Add(lib);
        await db.SaveChangesAsync();
        return lib;
    }

    /// <summary>
    /// 种一个条目及其文件行。<paramref name="dir"/> 是相对 <c>/test</c> 的目录，
    /// 文件路径为 <c>/test/{dir}/{name}</c>。
    /// </summary>
    private static async Task<MediaItem> SeedItemAsync(
        OpenListDbContext db, long libraryId, string title, string dir,
        params (string Name, long Size, int? Duration)[] files)
    {
        var item = new MediaItem
        {
            LibraryId = libraryId,
            Type = MediaLibraryType.Movie,
            Title = title,
            DirectoryPath = "/test/" + dir.Trim('/'),
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        foreach (var (fileName, size, duration) in files)
        {
            db.MediaFiles.Add(new MediaFile
            {
                ItemId = item.Id,
                Path = $"/test/{dir.Trim('/')}/{fileName}",
                Size = size,
                DurationSeconds = duration,
                CreatedAt = Now,
            });
        }
        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>一整套替身：内存库 + 挂在 <c>/test</c> 的内存文件系统 + 被测服务。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection _conn;

        public Harness()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            var options = new DbContextOptionsBuilder<OpenListDbContext>().UseSqlite(_conn).Options;
            Db = new OpenListDbContext(options, new DatabaseConfig());
            Db.Database.EnsureCreated();

            Memory = new MemoryFileSystem();
            Mount = new MountService();
            Mount.Root.Mount(new UPath("/test"), Memory);

            var fs = new FileSystemService(Mount);
            var libraries = new MediaLibraryService(Db);
            Service = new MediaCleanupService(Db, libraries, Mount, fs);
        }

        public OpenListDbContext Db { get; }
        public MemoryFileSystem Memory { get; }
        public MountService Mount { get; }
        public MediaCleanupService Service { get; }

        public void Write(string vfsPath, byte[] content) => Memory.WriteAllBytes(new UPath(MemPath(vfsPath)), content);

        public bool Exists(string vfsPath)
        {
            var path = new UPath(MemPath(vfsPath));
            return Memory.FileExists(path) || Memory.DirectoryExists(path);
        }

        public void Dispose()
        {
            Db.Dispose();
            _conn.Dispose();
        }
    }
}
