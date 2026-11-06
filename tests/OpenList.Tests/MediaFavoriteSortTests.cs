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
/// <c>GET /media/items?sort=favorited</c>（按当前用户收藏时间排序）。
///
/// 这条能力存在的唯一理由是让库页「收藏」列表与首页「最近收藏」同序：
/// 首页按状态行 <c>UpdatedAt</c> 倒序，库页此前只有 <c>sort=created</c>（按入库时间）。
/// 要钉住三件事：已收藏在前且按收藏时间、未收藏/无状态行恒在尾部、别人（其它用户）
/// 的收藏不影响我的顺序；再加一条与首页 <c>recently_favorited</c> 的直接比对，
/// 防止两边哪天又各自漂移。
/// </summary>
public class MediaFavoriteSortTests
{
    private static readonly User AdminUser = new() { Id = 1, Username = "admin", Role = UserRole.Admin };
    private static readonly User OtherUser = new() { Id = 2, Username = "other", Role = UserRole.Admin };

    /// <summary>已收藏的按收藏时间倒序在前；未收藏（B，有状态行但未收藏）与没有状态行（D）都在尾部，尾部按入库时间倒序。</summary>
    [Test]
    public async Task Favorited_Desc_Puts_Favorites_First_Then_Tail_By_Newest()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var libId = await SeedAsync(ctx, now);
        var svc = new MediaLibraryService(ctx);

        var resp = await svc.ListItemsAsync(
            new MediaItemQuery { LibraryId = libId, Sort = "favorited", PerPage = 20 }, AdminUser);

        await Assert.That(resp.Code).IsEqualTo(200);
        // A 收藏于 10 天前、C 收藏于 1 天前 → C 在前；尾部 D(昨天入库)、B(三天前入库)
        await Assert.That(Titles(resp.Data!)).IsEqualTo("C,A,D,B");
    }

    /// <summary>升序只反转「已收藏」这一组，尾部组仍按入库时间倒序（取消收藏后条目不会乱跳）。</summary>
    [Test]
    public async Task Favorited_Asc_Reverses_Favorite_Group_Only()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var libId = await SeedAsync(ctx, now);
        var svc = new MediaLibraryService(ctx);

        var resp = await svc.ListItemsAsync(
            new MediaItemQuery { LibraryId = libId, Sort = "favorited", Desc = false, PerPage = 20 }, AdminUser);

        await Assert.That(Titles(resp.Data!)).IsEqualTo("A,C,D,B");
    }

    /// <summary>
    /// 与首页同一口径：<c>state=favorite&amp;sort=favorited</c> 的顺序必须与
    /// <c>/media/home</c> 的 <c>recently_favorited</c> 一致（这是本次要收口的那条"对不齐"）。
    /// </summary>
    [Test]
    public async Task Favorite_List_Matches_Home_Recently_Favorited_Order()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var libId = await SeedAsync(ctx, now);
        var libraries = new MediaLibraryService(ctx);
        var catalog = new MediaCatalogService(ctx, libraries);

        var list = await libraries.ListItemsAsync(
            new MediaItemQuery { LibraryId = libId, State = "favorite", Sort = "favorited", PerPage = 20 },
            AdminUser);
        var home = await catalog.GetHomeAsync(18, AdminUser);

        await Assert.That(home.Code).IsEqualTo(200);
        await Assert.That(Titles(list.Data!)).IsEqualTo("C,A");
        await Assert.That(Titles(home.Data!.RecentlyFavorited)).IsEqualTo("C,A");
    }

    /// <summary>其它用户的收藏不能把我的条目顶到前面：D 被 user 2 收藏过，对 user 1 仍是尾部组。</summary>
    [Test]
    public async Task Other_Users_Favorites_Are_Ignored()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var libId = await SeedAsync(ctx, now);
        var itemD = await ctx.MediaItems.AsNoTracking().FirstAsync(i => i.Title == "D");
        ctx.UserMediaStates.Add(new UserMediaState
        {
            UserId = OtherUser.Id, ItemId = itemD.Id, Favorite = true, UpdatedAt = now,
        });
        await ctx.SaveChangesAsync();

        var svc = new MediaLibraryService(ctx);
        var resp = await svc.ListItemsAsync(
            new MediaItemQuery { LibraryId = libId, Sort = "favorited", PerPage = 20 }, AdminUser);

        await Assert.That(Titles(resp.Data!)).IsEqualTo("C,A,D,B");
    }

    /// <summary>与其它 <c>state</c> 组合：<c>state=played</c> 仍只返回看过的条目，但其中已收藏的排前面。</summary>
    [Test]
    public async Task Favorited_Sort_Composes_With_Other_State_Filters()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var libId = await SeedAsync(ctx, now);
        var svc = new MediaLibraryService(ctx);

        var resp = await svc.ListItemsAsync(
            new MediaItemQuery
            {
                LibraryId = libId, State = "played", Sort = "favorited", PerPage = 20,
            }, AdminUser);

        // 看过的是 A（已收藏）与 B（未收藏）；收藏优先 → A，随后 B
        await Assert.That(Titles(resp.Data!)).IsEqualTo("A,B");
    }

    // ------------------------------------------------------------------
    // 装置
    // ------------------------------------------------------------------

    /// <summary>
    /// 四个条目：A 最旧入库、D 最新入库。
    /// 状态行：A 收藏于 -10d（且看过）、C 收藏于 -1d、B 有状态行但未收藏（-2d 更新）、
    /// D 完全没有状态行 —— B 与 D 代表两种不同的"尾部组"来源，供 <c>state=played</c> 组合测试使用。
    /// </summary>
    private static async Task<long> SeedAsync(OpenListDbContext ctx, DateTimeOffset now)
    {
        var lib = new MediaLibrary
        {
            Name = "lib", Path = "/med", Type = MediaLibraryType.Movie,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.MediaLibraries.Add(lib);
        await ctx.SaveChangesAsync();

        MediaItem Item(string title, int createdAgoDays) => new()
        {
            LibraryId = lib.Id,
            Title = title,
            DirectoryPath = "/med/" + title,
            CreatedAt = now.AddDays(-createdAgoDays),
            UpdatedAt = now.AddDays(-createdAgoDays),
        };

        var a = Item("A", 4);
        var b = Item("B", 3);
        var c = Item("C", 2);
        var d = Item("D", 1);
        ctx.MediaItems.AddRange(a, b, c, d);
        await ctx.SaveChangesAsync();

        ctx.UserMediaStates.AddRange(
            new UserMediaState
            {
                UserId = AdminUser.Id, ItemId = a.Id, Favorite = true, Watched = true,
                UpdatedAt = now.AddDays(-10),
            },
            new UserMediaState
            {
                UserId = AdminUser.Id, ItemId = c.Id, Favorite = true,
                UpdatedAt = now.AddDays(-1),
            },
            new UserMediaState
            {
                UserId = AdminUser.Id, ItemId = b.Id, Watched = true,
                UpdatedAt = now.AddDays(-2),
            });
        await ctx.SaveChangesAsync();
        return lib.Id;
    }

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
