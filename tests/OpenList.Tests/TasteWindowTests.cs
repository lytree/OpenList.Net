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
        await Assert.That(TasteWindow.InWindow(true, false, null, default, null)).IsTrue();
    }

    /// <summary>有会话时完全按会话判定，状态行刚改过也不能把条目拉进窗口。</summary>
    [Test]
    public async Task InWindow_With_Sessions_Ignores_State_Row_Time()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var cutoff = TasteWindow.Cutoff(30, now);

        // 有会话但都在窗口外：即便状态行昨天刚改，也算"没有最近观看"
        await Assert.That(TasteWindow.InWindow(true, false, now.AddDays(-1), now.AddDays(-1), cutoff)).IsFalse();
        // 有会话在窗口内：即便状态行是两年前，也算
        await Assert.That(TasteWindow.InWindow(true, true, now.AddDays(-700), now.AddDays(-700), cutoff)).IsTrue();
        // 没有会话：回退到状态行时间
        await Assert.That(TasteWindow.InWindow(false, false, now.AddDays(-1), now.AddDays(-1), cutoff)).IsTrue();
    }

    /// <summary>多次会话合并：窗口内时长累加，最近观看时间取 <c>EndedAt ?? LastAt</c> 的较大者。</summary>
    [Test]
    public async Task MergeSessions_Sums_InWindow_Seconds_And_Picks_Latest_Activity()
    {
        var started = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var cutoff = started.AddDays(-30);
        var sessions = new[]
        {
            Session(1, "s1", started.AddDays(-1), started.AddDays(-1).AddMinutes(5), 30, ended: true),
            Session(1, "s2", started.AddDays(-3), started.AddDays(-3).AddMinutes(10), 50, ended: false),
            Session(1, "s3", started.AddDays(-45), started.AddDays(-45).AddMinutes(5), 100, ended: true),
        };

        var merged = TasteWindow.MergeSessions(sessions, cutoff);

        await Assert.That(merged.HasAny).IsTrue();
        await Assert.That(merged.HasInWindow).IsTrue();
        await Assert.That(merged.WatchedSeconds).IsEqualTo(80L);   // 只累加窗口内的 30 + 50
        // 窗口内最近一次活动：第二段会话未结束，用最后心跳（-3d + 10min）；
        // 第一段已结束，用 EndedAt（-1d + 5min）—— 后者更晚。
        await Assert.That(merged.LastAt).IsEqualTo(started.AddDays(-1).AddMinutes(5));

        var all = TasteWindow.MergeSessions(sessions, null);
        await Assert.That(all.WatchedSeconds).IsEqualTo(180L);

        var outOfWindow = TasteWindow.MergeSessions([sessions[2]], cutoff);
        await Assert.That(outOfWindow.HasAny).IsTrue();
        await Assert.That(outOfWindow.HasInWindow).IsFalse();
        await Assert.That(outOfWindow.WatchedSeconds).IsEqualTo(0L);
        await Assert.That(outOfWindow.LastAt).IsNull();
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

    /// <summary>
    /// 有会话时以会话为准：状态行时间既不把旧观看拉进窗口，也不把刚改收藏的旧会话拉进来。
    /// 同时钉住"总时长用会话秒数而不是进度秒数"。
    /// </summary>
    [Test]
    public async Task Taste_Profile_Uses_Watch_Sessions_For_Window()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;

        var a = Item("A", "Comedy", now);
        var b = Item("B", "Drama", now);
        var c = Item("C", "Drama", now);
        ctx.MediaItems.AddRange(a, b, c);
        await ctx.SaveChangesAsync();

        ctx.UserMediaStates.AddRange(
            // A：状态行 40 天前，但 2 天前真的看过 → 计入窗口
            new UserMediaState
            {
                UserId = AdminUser.Id, ItemId = a.Id, Watched = true, ProgressSeconds = 50,
                LastPlayedAt = now.AddDays(-40), UpdatedAt = now.AddDays(-40),
            },
            // B：状态行昨天刚改（取消收藏），但最近一次会话在 45 天前 → 不计入
            new UserMediaState
            {
                UserId = AdminUser.Id, ItemId = b.Id, Favorite = true, UpdatedAt = now.AddDays(-1),
            },
            // C：没有会话（旧数据）→ 回退到状态行时间，40 天前同样不计入
            new UserMediaState
            {
                UserId = AdminUser.Id, ItemId = c.Id, Favorite = true, UpdatedAt = now.AddDays(-40),
            });
        ctx.MediaWatchSessions.AddRange(
            Session(a.Id, "t-a", now.AddDays(-2), now.AddDays(-2), 60),
            Session(b.Id, "t-b", now.AddDays(-45), now.AddDays(-45), 120));
        await ctx.SaveChangesAsync();

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());

        var recent = (await svc.GetTasteProfileAsync(AdminUser, days: 30)).Data!;
        await Assert.That(recent.WindowDays).IsEqualTo(30);
        await Assert.That(recent.FavoriteCount).IsEqualTo(0);   // B 的收藏不算"最近看过"
        await Assert.That(recent.WatchedCount).IsEqualTo(1);    // A
        await Assert.That(recent.TotalWatchSeconds).IsEqualTo(60L); // 会话秒数，不是进度 50
        await Assert.That(recent.Genres.Count).IsEqualTo(1);
        await Assert.That(recent.Genres[0].Genre).IsEqualTo("Comedy");
        await Assert.That(recent.Genres[0].Weight).IsEqualTo(1);

        var all = (await svc.GetTasteProfileAsync(AdminUser)).Data!;
        await Assert.That(all.FavoriteCount).IsEqualTo(2);      // B + C
        await Assert.That(all.WatchedCount).IsEqualTo(1);
        await Assert.That(all.TotalWatchSeconds).IsEqualTo(180L); // A 60 + B 120
        await Assert.That(all.Genres[0].Genre).IsEqualTo("Drama");
        await Assert.That(all.Genres[0].Weight).IsEqualTo(4);   // B / C 收藏各 +2
    }

    /// <summary>同一条目多次观看：条目只计一次（计数/权重不叠加），时长按窗口内会话累加。</summary>
    [Test]
    public async Task Taste_Profile_Sums_Multiple_Sessions_For_The_Same_Item()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var item = Item("M", "Action", now);
        ctx.MediaItems.Add(item);
        await ctx.SaveChangesAsync();

        ctx.UserMediaStates.Add(new UserMediaState
        {
            UserId = AdminUser.Id, ItemId = item.Id, Watched = true, ProgressSeconds = 100,
            LastPlayedAt = now.AddDays(-1), UpdatedAt = now.AddDays(-1),
        });
        ctx.MediaWatchSessions.AddRange(
            Session(item.Id, "t-1", now.AddDays(-1), now.AddDays(-1), 30),
            Session(item.Id, "t-2", now.AddDays(-5), now.AddDays(-5), 50),
            Session(item.Id, "t-3", now.AddDays(-50), now.AddDays(-50), 100));
        await ctx.SaveChangesAsync();

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());

        var recent = (await svc.GetTasteProfileAsync(AdminUser, days: 30)).Data!;
        await Assert.That(recent.WatchedCount).IsEqualTo(1);       // 多次观看仍是"一个条目"
        await Assert.That(recent.TotalWatchSeconds).IsEqualTo(80L); // 30 + 50，窗口外的 100 不算
        await Assert.That(recent.Genres.Count).IsEqualTo(1);
        await Assert.That(recent.Genres[0].Weight).IsEqualTo(1);   // 权重不按观看次数叠加

        var all = (await svc.GetTasteProfileAsync(AdminUser)).Data!;
        await Assert.That(all.TotalWatchSeconds).IsEqualTo(180L);
    }

    /// <summary>没有会话的旧条目回退到状态行时间与进度秒数，旧库不会直接变空。</summary>
    [Test]
    public async Task Taste_Profile_Falls_Back_To_State_Row_Without_Sessions()
    {
        using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var item = Item("L", "Drama", now);
        ctx.MediaItems.Add(item);
        await ctx.SaveChangesAsync();

        ctx.UserMediaStates.Add(new UserMediaState
        {
            UserId = AdminUser.Id, ItemId = item.Id, Watched = true, ProgressSeconds = 60,
            LastPlayedAt = now.AddDays(-2), UpdatedAt = now.AddDays(-2),
        });
        await ctx.SaveChangesAsync();

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());

        var recent = (await svc.GetTasteProfileAsync(AdminUser, days: 30)).Data!;
        await Assert.That(recent.WatchedCount).IsEqualTo(1);
        await Assert.That(recent.TotalWatchSeconds).IsEqualTo(60L);

        var narrow = (await svc.GetTasteProfileAsync(AdminUser, days: 1)).Data!;
        await Assert.That(narrow.WatchedCount).IsEqualTo(0);
        await Assert.That(narrow.TotalWatchSeconds).IsEqualTo(0L);
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

    /// <summary>构造一段观看会话；<paramref name="ended"/> 为 true 时把 <c>EndedAt</c> 写成最后心跳时间。</summary>
    private static MediaWatchSession Session(
        long itemId, string token, DateTimeOffset startedAt, DateTimeOffset lastAt,
        long watchedSeconds, bool ended = true)
    {
        var session = new MediaWatchSession
        {
            UserId = AdminUser.Id,
            ItemId = itemId,
            Token = token,
            StartedAt = startedAt,
            LastAt = lastAt,
            WatchedSeconds = watchedSeconds,
        };
        if (ended) session.EndedAt = lastAt;
        return session;
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
