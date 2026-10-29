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
/// 口味画像的时间窗口（`GET /media/taste?days=`）。
///
/// 两个层次都要钉住：
/// - <see cref="TasteWindow"/> 是口径本身（归一天数、窗口起点、边界包含）；
/// - 服务层必须真的用了它 —— 只测纯函数的话，"忘了传 cutoff" 这种改动照样全绿。
/// </summary>
public class TasteWindowTests
{
    private static readonly User AdminUser = new() { Id = 1, Username = "admin", Role = UserRole.Admin };

    // ------------------------------------------------------------------
    // 口径
    // ------------------------------------------------------------------

    [Test]
    public async Task Normalize_Treats_Missing_And_NonPositive_As_Unlimited()
    {
        await Assert.That(TasteWindow.Normalize(null)).IsEqualTo(0);
        await Assert.That(TasteWindow.Normalize(0)).IsEqualTo(0);
        await Assert.That(TasteWindow.Normalize(-7)).IsEqualTo(0);
    }

    [Test]
    public async Task Normalize_Passes_Through_And_Clamps_Upper_Bound()
    {
        await Assert.That(TasteWindow.Normalize(1)).IsEqualTo(1);
        await Assert.That(TasteWindow.Normalize(30)).IsEqualTo(30);
        await Assert.That(TasteWindow.Normalize(TasteWindow.MaxDays)).IsEqualTo(TasteWindow.MaxDays);
        await Assert.That(TasteWindow.Normalize(TasteWindow.MaxDays + 1)).IsEqualTo(TasteWindow.MaxDays);
    }

    /// <summary>不限时必须返回 null 而不是一个极早的时间：过滤掉默认值 0 的行会静默少算。</summary>
    [Test]
    public async Task Cutoff_Is_Null_When_Unlimited_And_Backdated_Otherwise()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        await Assert.That(TasteWindow.Cutoff(null, now)).IsNull();
        await Assert.That(TasteWindow.Cutoff(0, now)).IsNull();
        await Assert.That(TasteWindow.Cutoff(30, now)).IsEqualTo(now.AddDays(-30));
    }

    [Test]
    public async Task Latest_Prefers_Whichever_Is_Newer()
    {
        var updated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var played = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await Assert.That(TasteWindow.Latest(played, updated)).IsEqualTo(played);
        await Assert.That(TasteWindow.Latest(updated, played)).IsEqualTo(played);
        await Assert.That(TasteWindow.Latest(null, updated)).IsEqualTo(updated);
    }

    /// <summary>边界取「含」：正好等于 cutoff 的行算窗口内，否则 `days=30` 会比用户预期少一天。</summary>
    [Test]
    public async Task InWindow_Includes_The_Cutoff_Instant()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var cutoff = TasteWindow.Cutoff(30, now)!.Value;

        await Assert.That(TasteWindow.InWindow(null, cutoff, cutoff)).IsTrue();
        await Assert.That(TasteWindow.InWindow(null, cutoff.AddTicks(-1), cutoff)).IsFalse();
        await Assert.That(TasteWindow.InWindow(null, cutoff.AddDays(1), cutoff)).IsTrue();
    }

    /// <summary>不限时，连 <c>UpdatedAt</c> 从未赋值的行也要算进来。</summary>
    [Test]
    public async Task InWindow_Is_Always_True_When_Unlimited()
    {
        await Assert.That(TasteWindow.InWindow(null, default, null)).IsTrue();
    }

    // ------------------------------------------------------------------
    // 服务层：窗口真的生效
    // ------------------------------------------------------------------

    [Test]
    public async Task Taste_Profile_Filters_By_Window()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;

        // 1) 40 天前收藏的《A》——窗口外
        var a = Item("A", "Drama", now);
        // 2) 2 天前看到 60 秒的《B》——窗口内
        var b = Item("B", "Comedy", now);
        // 3) 45 天前收藏、1 天前又播过（LastPlayedAt 比 UpdatedAt 新）的《C》——窗口内
        var c = Item("C", "Drama", now);
        ctx.MediaItems.AddRange(a, b, c);
        await ctx.SaveChangesAsync();

        ctx.UserMediaStates.AddRange(
            new UserMediaState { UserId = AdminUser.Id, ItemId = a.Id, Favorite = true, UpdatedAt = now.AddDays(-40) },
            new UserMediaState
            {
                UserId = AdminUser.Id, ItemId = b.Id, Watched = true, ProgressSeconds = 60,
                LastPlayedAt = now.AddDays(-2), UpdatedAt = now.AddDays(-2),
            },
            new UserMediaState
            {
                UserId = AdminUser.Id, ItemId = c.Id, Favorite = true,
                LastPlayedAt = now.AddDays(-1), UpdatedAt = now.AddDays(-45),
            });
        await ctx.SaveChangesAsync();

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());

        var recent = await svc.GetTasteProfileAsync(AdminUser, days: 30);
        await Assert.That(recent.Code).IsEqualTo(200);
        var r = recent.Data!;
        await Assert.That(r.WindowDays).IsEqualTo(30);
        await Assert.That(r.FavoriteCount).IsEqualTo(1);   // 只有 C
        await Assert.That(r.WatchedCount).IsEqualTo(1);    // 只有 B
        await Assert.That(r.TotalWatchSeconds).IsEqualTo(60L);
        await Assert.That(r.Genres.Count).IsEqualTo(2);
        await Assert.That(r.Genres[0].Genre).IsEqualTo("Drama");
        await Assert.That(r.Genres[0].Weight).IsEqualTo(2); // C 收藏 +2
        await Assert.That(r.Genres[1].Genre).IsEqualTo("Comedy");
        await Assert.That(r.Genres[1].Weight).IsEqualTo(1); // B 看过 +1

        var all = await svc.GetTasteProfileAsync(AdminUser);
        var f = all.Data!;
        await Assert.That(f.WindowDays).IsEqualTo(0);
        await Assert.That(f.FavoriteCount).IsEqualTo(2);      // A + C
        await Assert.That(f.WatchedCount).IsEqualTo(1);
        await Assert.That(f.Genres[0].Genre).IsEqualTo("Drama");
        await Assert.That(f.Genres[0].Weight).IsEqualTo(4);   // A +2 / C +2
    }

    /// <summary>`days=1` 只留最近一天：C 是 Yesterday，B 是两天前 → 都不算。</summary>
    [Test]
    public async Task Taste_Profile_Narrow_Window_Can_Be_Empty()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var item = Item("B", "Comedy", now);
        ctx.MediaItems.Add(item);
        await ctx.SaveChangesAsync();
        ctx.UserMediaStates.Add(new UserMediaState
        {
            UserId = AdminUser.Id, ItemId = item.Id, Watched = true, ProgressSeconds = 60,
            LastPlayedAt = now.AddDays(-2), UpdatedAt = now.AddDays(-2),
        });
        await ctx.SaveChangesAsync();

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());
        var resp = await svc.GetTasteProfileAsync(AdminUser, days: 1);
        var p = resp.Data!;

        await Assert.That(p.WatchedCount).IsEqualTo(0);
        await Assert.That(p.Genres.Count).IsEqualTo(0);
    }

    // ------------------------------------------------------------------
    // 装置
    // ------------------------------------------------------------------

    private static MediaItem Item(string title, string genre, DateTimeOffset now)
    {
        var item = new MediaItem
        {
            LibraryId = 1,
            Title = title,
            DirectoryPath = "/f/" + title,
            CreatedAt = now,
            UpdatedAt = now,
        };
        item.SetGenres([genre]);
        return item;
    }

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

    /// <summary>IMediaLibraryService 假件：口味统计只用得到可见性判定。</summary>
    private sealed class FakeLibraryService : IMediaLibraryService
    {
        public Task<Resp<MediaLibrary>> CreateAsync(CreateMediaLibraryReq req, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaLibrary>.Fail("not implemented", 500));
        public Task<Resp> UpdateAsync(long id, UpdateMediaLibraryReq req, User user, CancellationToken ct = default)
            => Task.FromResult(Resp.Fail("not implemented", 500));
        public Task<Resp> DeleteAsync(long id, User user, CancellationToken ct = default)
            => Task.FromResult(Resp.Fail("not implemented", 500));
        public Task<Resp<List<MediaLibrary>>> ListAsync(User user, CancellationToken ct = default)
            => Task.FromResult(Resp<List<MediaLibrary>>.Ok([new MediaLibrary { Id = 1, Name = "lib", Path = "/f" }]));
        public Task<Resp<MediaLibrary>> GetAsync(long id, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaLibrary>.Fail("not implemented", 500));
        public Task<Resp<PageResp<MediaItem>>> ListItemsAsync(MediaItemQuery query, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<PageResp<MediaItem>>.Fail("not implemented", 500));
        public Task<Resp<MediaItem>> GetItemAsync(long itemId, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaItem>.Fail("not implemented", 500));
        public Task<Resp<List<MediaFile>>> ListItemFilesAsync(long itemId, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<List<MediaFile>>.Fail("not implemented", 500));
        public bool CanSeeLibrary(User user, MediaLibrary library) => true;
        public bool CanSeeItem(User user, MediaItem item) => true;
    }
}
