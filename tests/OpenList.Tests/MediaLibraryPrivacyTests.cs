using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;
using OpenList.Media.Services;

namespace OpenList.Tests;

/// <summary>
/// 媒体库的「公开 / 私有」分区。
///
/// 口径只有一条：**私有库（<see cref="MediaLibrary.Private"/>）对匿名游客完全不存在**，
/// 对已登录用户完全正常。这条规则落在 <see cref="MediaLibraryService.CanSeeLibrary"/> 一处，
/// 所有聚合（首页 / facets / 人物 / 片商 / 系列 / 统计）都经由它筛，所以这里按「入口」而不是
/// 「页面」来钉：
/// <list type="number">
/// <item>库列表 → 游客只看到公开库；</item>
/// <item>跨库条目列表 / 单库条目列表 → 私有条目不出现在游客结果里，带 id 直接问也给 404；</item>
/// <item>单条详情 / 文件清单 → 404（不是 403：403 等于确认这条 id 存在）；</item>
/// <item>首页聚合 / 题材统计 → 计数里不含私有条目；</item>
/// <item>登录用户 → 以上全部照常，公开与私有一视同仁。</item>
/// </list>
/// </summary>
public class MediaLibraryPrivacyTests
{
    /// <summary>真实登录用户（Id &gt; 0）。</summary>
    private static readonly User Admin = new() { Id = 1, Username = "admin" };

    /// <summary>匿名访客（<c>ClaimsPrincipal.ToUser()</c> 对未认证请求的产出）。</summary>
    private static readonly User Guest = new() { Id = 0 };

    [Test]
    public async Task Guest_Sees_Only_Public_Libraries()
    {
        using var ctx = BuildContext();
        var (pub, priv) = await SeedAsync(ctx);
        var svc = new MediaLibraryService(ctx);

        var guest = await svc.ListAsync(Guest);
        var admin = await svc.ListAsync(Admin);

        await Assert.That(Names(guest.Data!)).IsEqualTo("Public");
        await Assert.That(Names(admin.Data!)).IsEqualTo("Public,Private");
        await Assert.That(guest.Data!.Single().Id).IsEqualTo(pub);
        await Assert.That(admin.Data!.Any(l => l.Id == priv)).IsTrue();
    }

    [Test]
    public async Task Guest_Gets_404_For_Private_Library_By_Id()
    {
        using var ctx = BuildContext();
        var (pub, priv) = await SeedAsync(ctx);
        var svc = new MediaLibraryService(ctx);

        // 公开库照常；私有库按「不存在」处理，不给 403 那种存在性信号。
        await Assert.That((await svc.GetAsync(pub, Guest)).Code).IsEqualTo(200);
        await Assert.That((await svc.GetAsync(priv, Guest)).Code).IsEqualTo(404);
        await Assert.That((await svc.GetAsync(priv, Admin)).Code).IsEqualTo(200);
    }

    [Test]
    public async Task Cross_Library_List_Hides_Private_Items_From_Guest()
    {
        using var ctx = BuildContext();
        await SeedAsync(ctx);
        var svc = new MediaLibraryService(ctx);

        var guest = await svc.ListItemsAsync(new MediaItemQuery { PerPage = 50 }, Guest);
        var admin = await svc.ListItemsAsync(new MediaItemQuery { PerPage = 50 }, Admin);

        await Assert.That(Titles(guest.Data!)).IsEqualTo("Public A");
        await Assert.That(Titles(admin.Data!)).IsEqualTo("Private A,Public A");
    }

    [Test]
    public async Task Guest_Cannot_List_Private_Library_Items_By_Id()
    {
        using var ctx = BuildContext();
        var (_, priv) = await SeedAsync(ctx);
        var svc = new MediaLibraryService(ctx);

        var guest = await svc.ListItemsAsync(new MediaItemQuery { LibraryId = priv, PerPage = 50 }, Guest);
        var admin = await svc.ListItemsAsync(new MediaItemQuery { LibraryId = priv, PerPage = 50 }, Admin);

        await Assert.That(guest.Code).IsEqualTo(404);
        await Assert.That(Titles(admin.Data!)).IsEqualTo("Private A");
    }

    [Test]
    public async Task Guest_Cannot_Read_Private_Item_Or_Its_Files()
    {
        using var ctx = BuildContext();
        var (_, priv) = await SeedAsync(ctx);
        var svc = new MediaLibraryService(ctx);
        var item = await ctx.MediaItems.FirstAsync(i => i.LibraryId == priv);

        var itemGuest = await svc.GetItemAsync(item.Id, Guest);
        var filesGuest = await svc.ListItemFilesAsync(item.Id, Guest);
        var itemAdmin = await svc.GetItemAsync(item.Id, Admin);

        await Assert.That(itemGuest.Code).IsEqualTo(404);
        await Assert.That(filesGuest.Code).IsEqualTo(404);
        await Assert.That(itemAdmin.Code).IsEqualTo(200);
        // 文件清单带真实 VFS 路径，私有库对游客连路径都不能露。
        await Assert.That((await svc.ListItemFilesAsync(item.Id, Admin)).Data!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Home_And_Facets_Exclude_Private_Items_For_Guest()
    {
        using var ctx = BuildContext();
        await SeedAsync(ctx);
        var libraries = new MediaLibraryService(ctx);
        var catalog = new MediaCatalogService(ctx, libraries);

        var guestHome = await catalog.GetHomeAsync(20, Guest);
        var adminHome = await catalog.GetHomeAsync(20, Admin);
        var guestFacets = await catalog.GetFacetsAsync(null, Guest);
        var adminFacets = await catalog.GetFacetsAsync(null, Admin);

        await Assert.That(Titles(guestHome.Data!.RecentlyAdded)).IsEqualTo("Public A");
        await Assert.That(adminHome.Data!.RecentlyAdded.Count).IsEqualTo(2);
        // 私有条目挂着一个独有题材，「最近入库」与题材计数都必须把它排除干净。
        await Assert.That(guestFacets.Data!.Total).IsEqualTo(1);
        await Assert.That(adminFacets.Data!.Total).IsEqualTo(2);
        await Assert.That(guestFacets.Data!.Genres.Any(g => g.Value == "Secret")).IsFalse();
    }

    [Test]
    public async Task Private_Flag_Round_Trips_Through_Create_And_Update()
    {
        using var ctx = BuildContext();
        var svc = new MediaLibraryService(ctx);

        var created = await svc.CreateAsync(
            new CreateMediaLibraryReq { Name = "Vault", Path = "/vault", Private = true }, Admin);
        await Assert.That(created.Data!.Private).IsTrue();

        await svc.UpdateAsync(created.Data.Id, new UpdateMediaLibraryReq { Private = false }, Admin);
        var reloaded = await svc.GetAsync(created.Data.Id, Admin);
        await Assert.That(reloaded.Data!.Private).IsFalse();

        // 不传 Private（null）时不得被顺手改成 false —— 表单只改名字时必须保留原可见性。
        await svc.UpdateAsync(created.Data.Id, new UpdateMediaLibraryReq { Private = true }, Admin);
        await svc.UpdateAsync(created.Data.Id, new UpdateMediaLibraryReq { Name = "Vault 2" }, Admin);
        await Assert.That((await svc.GetAsync(created.Data.Id, Admin)).Data!.Private).IsTrue();
    }

    // ------------------------------------------------------------------
    // 装置
    // ------------------------------------------------------------------

    private static async Task<(long Public, long Private)> SeedAsync(OpenListDbContext ctx)
    {
        var now = DateTimeOffset.UtcNow;
        var pub = new MediaLibrary
        {
            Name = "Public", Path = "/pub", Type = MediaLibraryType.Movie, CreatedAt = now, UpdatedAt = now,
        };
        var priv = new MediaLibrary
        {
            Name = "Private", Path = "/priv", Type = MediaLibraryType.Movie, Private = true,
            CreatedAt = now.AddSeconds(1), UpdatedAt = now.AddSeconds(1),
        };
        ctx.MediaLibraries.AddRange(pub, priv);
        await ctx.SaveChangesAsync();

        var itemPub = new MediaItem
        {
            LibraryId = pub.Id, Title = "Public A", DirectoryPath = "/pub/a",
            GenresJson = "[\"Drama\"]", CreatedAt = now, UpdatedAt = now,
        };
        var itemPriv = new MediaItem
        {
            LibraryId = priv.Id, Title = "Private A", DirectoryPath = "/priv/a",
            GenresJson = "[\"Secret\"]", CreatedAt = now.AddSeconds(1), UpdatedAt = now.AddSeconds(1),
        };
        ctx.MediaItems.AddRange(itemPub, itemPriv);
        await ctx.SaveChangesAsync();

        ctx.MediaFiles.AddRange(
            new MediaFile { ItemId = itemPub.Id, Path = "/pub/a.mkv", CreatedAt = now },
            new MediaFile { ItemId = itemPriv.Id, Path = "/priv/a.mkv", CreatedAt = now });
        await ctx.SaveChangesAsync();

        return (pub.Id, priv.Id);
    }

    private static string Names(IEnumerable<MediaLibrary> libs) => string.Join(",", libs.Select(l => l.Name));

    private static string Titles(PageResp<MediaItem> page) => string.Join(",", page.Content.Select(i => i.Title));

    private static string Titles(IEnumerable<MediaItem> items) => string.Join(",", items.Select(i => i.Title));

    private static OpenListDbContext BuildContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<OpenListDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = new OpenListDbContext(opts, new DatabaseConfig());
        ctx.Database.EnsureCreated();
        return ctx;
    }
}
