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
/// 观看会话（`/media/watch/*`）。
///
/// 这张表的全部价值在于"看了多久"这件事**不能靠时间差算**：暂停、拖进度条、
/// 关掉页面放着不动，都会让 `结束时间 - 开始时间` 偏大或偏小。所以累计只认
/// 「向前的小步进」，其余一律不计 —— 这组用例主要钉这一条，以及三个结束边界。
/// </summary>
public class WatchSessionTests
{
    private static readonly User Admin = new() { Id = 1, Username = "admin" };
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task First_Report_Creates_An_Open_Session()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        var resp = await svc.ReportAsync(new ReportWatchReq
        {
            Token = "t1", ItemId = a.Id, FileId = 11, PositionSeconds = 120,
        }, Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        var s = resp.Data!;
        await Assert.That(s.StartedAt).IsEqualTo(Now);
        await Assert.That(s.StartPositionSeconds).IsEqualTo(120L);
        await Assert.That(s.WatchedSeconds).IsEqualTo(0L);   // 起播那一下不算看过
        await Assert.That(s.EndedAt).IsNull();
    }

    [Test]
    public async Task Steady_Heartbeat_Accumulates_Watched_Seconds()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await Report(svc, "t1", a.Id, 0);
        await Report(svc, "t1", a.Id, 10);
        var resp = await Report(svc, "t1", a.Id, 20);

        await Assert.That(resp.Data!.WatchedSeconds).IsEqualTo(20L);
        await Assert.That(resp.Data.MaxPositionSeconds).IsEqualTo(20L);
    }

    /// <summary>拖进度条不是"看了"。向前跳 20 分钟要忽略，倒退更不该加。</summary>
    [Test]
    public async Task Seek_Jumps_Are_Not_Counted_As_Watching()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await Report(svc, "t1", a.Id, 0);
        var jumped = await Report(svc, "t1", a.Id, 1200);   // 向前拖 20 分钟
        await Assert.That(jumped.Data!.WatchedSeconds).IsEqualTo(0L);
        await Assert.That(jumped.Data.MaxPositionSeconds).IsEqualTo(1200L);

        var back = await Report(svc, "t1", a.Id, 60);       // 拖回来
        await Assert.That(back.Data!.WatchedSeconds).IsEqualTo(0L);

        var tick = await Report(svc, "t1", a.Id, 70);       // 恢复播放
        await Assert.That(tick.Data!.WatchedSeconds).IsEqualTo(10L);
    }

    [Test]
    public async Task Closing_Stamps_End_And_Completed()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await Report(svc, "t1", a.Id, 0);
        var resp = await svc.ReportAsync(new ReportWatchReq
        {
            Token = "t1", ItemId = a.Id, PositionSeconds = 600, Closing = true, Completed = true,
        }, Admin);

        await Assert.That(resp.Data!.EndedAt).IsEqualTo(Now);
        await Assert.That(resp.Data.Completed).IsTrue();
    }

    /// <summary>关播放器后那个兜底补写会再打一次；它不能把 EndedAt 抹掉或把时长加一遍。</summary>
    [Test]
    public async Task Reports_After_Closing_Are_Ignored()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await Report(svc, "t1", a.Id, 0);
        var closed = await svc.ReportAsync(new ReportWatchReq
        {
            Token = "t1", ItemId = a.Id, PositionSeconds = 100, Closing = true,
        }, Admin);
        var late = await Report(svc, "t1", a.Id, 130);

        await Assert.That(late.Code).IsEqualTo(200);
        await Assert.That(late.Data!.WatchedSeconds).IsEqualTo(closed.Data!.WatchedSeconds);
        await Assert.That(late.Data.EndedAt).IsEqualTo(Now);
    }

    /// <summary>同一个 token 换条目 = 客户端复用 bug，宁可 400 也不要把两段历史糅在一起。</summary>
    [Test]
    public async Task Token_Reused_For_Another_Item_Is_Rejected()
    {
        using var ctx = BuildContext();
        var (a, b, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await Report(svc, "t1", a.Id, 0);
        var resp = await Report(svc, "t1", b.Id, 0);

        await Assert.That(resp.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Invalid_Input_Is_Rejected()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await Assert.That((await Report(svc, "  ", a.Id, 0)).Code).IsEqualTo(400);
        await Assert.That((await Report(svc, "t1", a.Id, -1)).Code).IsEqualTo(400);
        await Assert.That((await Report(svc, "t1", 9999, 0)).Code).IsEqualTo(404);
    }

    [Test]
    public async Task History_Aggregates_By_Item_And_Skips_Invisible()
    {
        using var ctx = BuildContext();
        var (a, b, hidden) = await SeedItemsAsync(ctx);
        ctx.MediaWatchSessions.AddRange(
            Session(a.Id, "a1", Now.AddDays(-2), 100, completed: false),
            Session(a.Id, "a2", Now.AddDays(-1), 50, completed: true),
            Session(b.Id, "b1", Now.AddDays(-9), 30, completed: false),
            Session(hidden.Id, "h1", Now.AddDays(-1), 999, completed: true));
        await ctx.SaveChangesAsync();

        var svc = NewService(ctx);
        var resp = await svc.ListHistoryAsync(30, 12, Admin);
        var rows = resp.Data!;

        await Assert.That(rows.Count).IsEqualTo(2);                 // hidden 被剔除
        await Assert.That(rows[0].ItemId).IsEqualTo(a.Id);          // 最近的在前
        await Assert.That(rows[0].WatchedSeconds).IsEqualTo(150L);
        await Assert.That(rows[0].Completed).IsTrue();
        await Assert.That(rows[1].ItemId).IsEqualTo(b.Id);
    }

    [Test]
    public async Task Stats_Fill_Zero_Days_And_Sum_Window()
    {
        using var ctx = BuildContext();
        var (a, b, hidden) = await SeedItemsAsync(ctx);
        ctx.MediaWatchSessions.AddRange(
            Session(a.Id, "a1", Now.AddDays(-1), 120, completed: false),
            Session(b.Id, "b1", Now.AddDays(-1), 60, completed: false),
            Session(hidden.Id, "h1", Now.AddDays(-1), 1000, completed: false));
        await ctx.SaveChangesAsync();

        var svc = NewService(ctx);
        var resp = await svc.GetStatsAsync(3, Admin);
        var stats = resp.Data!;

        await Assert.That(stats.WindowDays).IsEqualTo(3);
        await Assert.That(stats.Days.Count).IsEqualTo(3);
        await Assert.That(stats.Days[^1].Date).IsEqualTo("2026-10-29");
        await Assert.That(stats.Days[^2].Seconds).IsEqualTo(180L);   // 昨天：120 + 60
        await Assert.That(stats.Days[^3].Seconds).IsEqualTo(0L);     // 补零
        await Assert.That(stats.TotalSeconds).IsEqualTo(180L);       // hidden 不计
    }

    /// <summary>留存清理按 <c>LastAt</c> 判定：老会话删掉；开始很早但仍在心跳的会话保留。</summary>
    [Test]
    public async Task Prune_Removes_Sessions_Whose_LastActivity_Is_Too_Old()
    {
        using var ctx = BuildContext();
        var (a, b, _) = await SeedItemsAsync(ctx);

        ctx.MediaWatchSessions.AddRange(
            Session(a.Id, "old", Now.AddDays(-60), 100, false),
            Session(a.Id, "long-running", Now.AddDays(-60), 100, false),
            Session(b.Id, "recent", Now.AddDays(-2), 100, true));
        await ctx.SaveChangesAsync();

        // 长会话：开始于 60 天前，但一小时前还有心跳、尚未结束 —— 不能被清理掉。
        var longRunning = ctx.MediaWatchSessions.Single(s => s.Token == "long-running");
        longRunning.LastAt = Now.AddHours(-1);
        longRunning.EndedAt = null;
        await ctx.SaveChangesAsync();

        var removed = await NewService(ctx).PruneAsync(Now.AddDays(-30));

        await Assert.That(removed).IsEqualTo(1);
        var remaining = await ctx.MediaWatchSessions.AsNoTracking().Select(s => s.Token).ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(2);
        await Assert.That(remaining.Contains("long-running")).IsTrue();
        await Assert.That(remaining.Contains("recent")).IsTrue();
    }

    // ------------------------------------------------------------------
    // Peach 口径的度量：播放次数、最远/离开比例、拖动次数、划水
    // ------------------------------------------------------------------

    /// <summary>播放次数按**会话**计，不按心跳计 —— 一条两小时的片子只有一次播放。</summary>
    [Test]
    public async Task Play_Count_Counts_Sessions_Not_Heartbeats()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await Report(svc, "t1", a.Id, 0);
        await Report(svc, "t1", a.Id, 10);
        await Report(svc, "t1", a.Id, 20);

        await Assert.That((await StateOf(ctx, a.Id)).PlayCount).IsEqualTo(1);

        // 换一个 token 才是"又点了一次播放"
        await Report(svc, "t2", a.Id, 0);
        await Assert.That((await StateOf(ctx, a.Id)).PlayCount).IsEqualTo(2);
    }

    /// <summary>拖动次数按增量累加：客户端每发只报"这一段新增几次"，服务端相加。</summary>
    [Test]
    public async Task Seek_Count_Accumulates_From_Increments()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await Report(svc, "t1", a.Id, 0);
        await ReportFull(svc, "t1", a.Id, 5, seeks: 3);
        await ReportFull(svc, "t1", a.Id, 9, seeks: 2);

        var session = await ctx.MediaWatchSessions.AsNoTracking().SingleAsync(s => s.Token == "t1");
        await Assert.That(session.SeekCount).IsEqualTo(5);
        await Assert.That((await StateOf(ctx, a.Id)).SeekCount).IsEqualTo(5);
    }

    /// <summary>最远/离开比例拿客户端上报的片长当分母，写进会话与条目两层。</summary>
    [Test]
    public async Task Max_Reached_And_Leave_Ratio_Use_Reported_Duration()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        var resp = await ReportFull(svc, "t1", a.Id, 500, duration: 1000);

        await Assert.That(resp.Data!.MaxReached).IsEqualTo(0.5);
        // 会话还开着：离开位置没定稿，只有最远比例在跟着走
        await Assert.That(resp.Data.LeaveRatio).IsNull();

        var state = await StateOf(ctx, a.Id);
        await Assert.That(state.MaxReached).IsEqualTo(0.5);
        await Assert.That(state.LeaveRatio).IsEqualTo(0.5);
        await Assert.That(state.DurationSeconds).IsEqualTo(1000L);
    }

    /// <summary>播完记 100%，而不是"最后一发心跳的位置" —— 最后一发未必落在片尾。</summary>
    [Test]
    public async Task Completed_Session_Records_Full_Leave_Ratio()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await ReportFull(svc, "t1", a.Id, 500, duration: 1000);
        var resp = await ReportFull(svc, "t1", a.Id, 500, duration: 1000, closing: true, completed: true);

        await Assert.That(resp.Data!.LeaveRatio).IsEqualTo(1.0);
        await Assert.That((await StateOf(ctx, a.Id)).LeaveRatio).IsEqualTo(1.0);
    }

    /// <summary>片长未知时比例整体不算：宁可留空，也不拿别的长度凑一个分母。</summary>
    [Test]
    public async Task Unknown_Duration_Leaves_Ratios_Untouched()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        var resp = await Report(svc, "t1", a.Id, 100);

        await Assert.That(resp.Data!.MaxReached).IsEqualTo(0.0);
        await Assert.That(resp.Data.LeaveRatio).IsNull();
        var state = await StateOf(ctx, a.Id);
        await Assert.That(state.LeaveRatio).IsNull();
        await Assert.That(state.DurationSeconds).IsNull();
    }

    /// <summary>划水：拖到 70% 却只真看了 30%，是找片段不是看片。</summary>
    [Test]
    public async Task Skimmed_Flags_Jumped_But_Not_Watched()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await ReportFull(svc, "t1", a.Id, 0, duration: 1000);
        await ReportFull(svc, "t1", a.Id, 100, duration: 1000);
        await ReportFull(svc, "t1", a.Id, 200, duration: 1000);
        await ReportFull(svc, "t1", a.Id, 300, duration: 1000);
        // 向前拖到 70%：不计入观看时长，但最远位置到了
        await ReportFull(svc, "t1", a.Id, 700, duration: 1000);

        var state = await StateOf(ctx, a.Id);
        await Assert.That(state.PlaySeconds).IsEqualTo(300L);
        await Assert.That(state.MaxReached).IsEqualTo(0.7);
        await Assert.That(state.Skimmed).IsTrue();
    }

    /// <summary>老实看完前 70% 不算划水 —— 判据比的是"真看的比例"和"最远位置"的差。</summary>
    [Test]
    public async Task Steady_Watching_Through_The_Same_Point_Is_Not_Skimmed()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await ReportFull(svc, "t1", a.Id, 0, duration: 1000);
        for (var position = 100; position <= 700; position += 100)
            await ReportFull(svc, "t1", a.Id, position, duration: 1000);

        var state = await StateOf(ctx, a.Id);
        await Assert.That(state.MaxReached).IsEqualTo(0.7);
        await Assert.That(state.Skimmed).IsFalse();
    }

    /// <summary>度量按用户隔离：同一部片子两个人各记各的。</summary>
    [Test]
    public async Task Metrics_Are_Isolated_Per_User()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);
        var bob = new User { Id = 2, Username = "bob" };

        await ReportFull(svc, "t1", a.Id, 900, duration: 1000);
        await ReportFull(svc, "t1", a.Id, 900, duration: 1000, closing: true, completed: true);
        await ReportFull(svc, "b1", a.Id, 100, duration: 1000, user: bob);

        var admin = await StateOf(ctx, a.Id);
        var other = await StateOf(ctx, a.Id, bob.Id);

        await Assert.That(admin.PlayCount).IsEqualTo(1);
        await Assert.That(admin.LeaveRatio).IsEqualTo(1.0);
        await Assert.That(other.PlayCount).IsEqualTo(1);
        await Assert.That(other.LeaveRatio).IsEqualTo(0.1);
    }

    /// <summary>历史行同时带窗口内与会话级两层读数：切时间窗不该让续播位置跟着变。</summary>
    [Test]
    public async Task History_Exposes_Item_Level_Metrics()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        await ReportFull(svc, "t1", a.Id, 0, duration: 1000);
        // 分两步走：单步上限 300 秒，一次跨 400 秒会被当成拖进度条而不计入观看时长
        await ReportFull(svc, "t1", a.Id, 200, duration: 1000);
        await ReportFull(svc, "t1", a.Id, 400, duration: 1000, seeks: 2);
        await ReportFull(svc, "t1", a.Id, 400, duration: 1000, closing: true);

        var rows = (await svc.ListHistoryAsync(30, 12, Admin)).Data!;
        await Assert.That(rows.Count).IsEqualTo(1);

        var row = rows[0];
        await Assert.That(row.WatchedSeconds).IsEqualTo(400L);   // 窗口内真的看了多久
        await Assert.That(row.PlayCount).IsEqualTo(1);
        await Assert.That(row.PlaySeconds).IsEqualTo(400L);
        await Assert.That(row.MaxReached).IsEqualTo(0.4);
        await Assert.That(row.LeaveRatio).IsEqualTo(0.4);
        await Assert.That(row.SeekCount).IsEqualTo(2);
        await Assert.That(row.Skimmed).IsFalse();
    }

    /// <summary>
    /// 心跳不能写续播进度：前端播完时会单独把 `progress_seconds` 清 0，
    /// 心跳若也写一列，两个请求谁后到谁说了算，表现为「看完了下次还从 40 分钟续播」。
    /// </summary>
    [Test]
    public async Task Heartbeat_Does_Not_Touch_Resume_Progress()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);
        var svc = NewService(ctx);

        ctx.UserMediaStates.Add(new UserMediaState
        {
            UserId = Admin.Id, ItemId = a.Id, ProgressSeconds = 0, UpdatedAt = Now,
        });
        await ctx.SaveChangesAsync();

        await ReportFull(svc, "t1", a.Id, 2400, duration: 5400);

        var state = await StateOf(ctx, a.Id);
        await Assert.That(state.ProgressSeconds).IsEqualTo(0L);
        await Assert.That(state.LeaveRatio).IsNotNull();
    }

    // ------------------------------------------------------------------
    // 装置
    // ------------------------------------------------------------------

    private static async Task<UserMediaState> StateOf(OpenListDbContext ctx, long itemId, long userId = 1)
        => await ctx.UserMediaStates.AsNoTracking()
            .SingleAsync(s => s.UserId == userId && s.ItemId == itemId);

    private static MediaWatchSession Session(long itemId, string token, DateTimeOffset startedAt, long watched, bool completed)
        => new()
        {
            UserId = Admin.Id,
            ItemId = itemId,
            Token = token,
            StartedAt = startedAt,
            LastAt = startedAt.AddMinutes(5),
            EndedAt = startedAt.AddMinutes(5),
            StartPositionSeconds = 0,
            LastPositionSeconds = watched,
            MaxPositionSeconds = watched,
            WatchedSeconds = watched,
            Completed = completed,
        };

    private static Task<Resp<MediaWatchSession>> Report(
        IWatchSessionService svc, string token, long itemId, long position)
        => svc.ReportAsync(new ReportWatchReq { Token = token, ItemId = itemId, PositionSeconds = position }, Admin);

    /// <summary>带上片长/拖动/收尾标志的完整上报（度量类用例用）。</summary>
    private static Task<Resp<MediaWatchSession>> ReportFull(
        IWatchSessionService svc, string token, long itemId, long position,
        long? duration = null, int seeks = 0, bool closing = false, bool completed = false, User? user = null)
        => svc.ReportAsync(new ReportWatchReq
        {
            Token = token,
            ItemId = itemId,
            PositionSeconds = position,
            DurationSeconds = duration,
            Seeks = seeks,
            Closing = closing,
            Completed = completed,
        }, user ?? Admin);

    private static WatchSessionService NewService(OpenListDbContext ctx)
        => new(ctx, new FakeLibraryService(), new FixedTime(Now));

    private static async Task<(MediaItem A, MediaItem B, MediaItem Hidden)> SeedItemsAsync(OpenListDbContext ctx)
    {
        var a = new MediaItem { LibraryId = 1, Title = "A", DirectoryPath = "/visible/a", CreatedAt = Now, UpdatedAt = Now };
        var b = new MediaItem { LibraryId = 1, Title = "B", DirectoryPath = "/visible/b", CreatedAt = Now, UpdatedAt = Now };
        var hidden = new MediaItem { LibraryId = 1, Title = "C", DirectoryPath = "/secret/c", CreatedAt = Now, UpdatedAt = Now };
        ctx.MediaItems.AddRange(a, b, hidden);
        await ctx.SaveChangesAsync();
        return (a, b, hidden);
    }

    private static OpenListDbContext BuildContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<OpenListDbContext>().UseSqlite(conn).Options;
        var ctx = new OpenListDbContext(opts, new DatabaseConfig());
        ctx.Database.EnsureCreated();
        return ctx;
    }

    /// <summary>固定时间的 TimeProvider：窗口与时间戳断言不能跟着真实时钟漂。</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>IMediaLibraryService 假件：`/secret` 前缀不可见。</summary>
    private sealed class FakeLibraryService : IMediaLibraryService
    {
        public Task<Resp<MediaLibrary>> CreateAsync(CreateMediaLibraryReq req, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaLibrary>.Fail("not implemented", 500));
        public Task<Resp> UpdateAsync(long id, UpdateMediaLibraryReq req, User user, CancellationToken ct = default)
            => Task.FromResult(Resp.Fail("not implemented", 500));
        public Task<Resp> DeleteAsync(long id, User user, CancellationToken ct = default)
            => Task.FromResult(Resp.Fail("not implemented", 500));
        public Task<Resp<List<MediaLibrary>>> ListAsync(User user, CancellationToken ct = default)
            => Task.FromResult(Resp<List<MediaLibrary>>.Ok([new MediaLibrary { Id = 1, Name = "lib", Path = "/" }]));
        public Task<Resp<MediaLibrary>> GetAsync(long id, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaLibrary>.Fail("not implemented", 500));
        public Task<Resp<PageResp<MediaItem>>> ListItemsAsync(MediaItemQuery query, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<PageResp<MediaItem>>.Fail("not implemented", 500));
        public Task<Resp<MediaItem>> GetItemAsync(long itemId, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaItem>.Fail("not implemented", 500));
        public Task<Resp<List<MediaFile>>> ListItemFilesAsync(long itemId, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<List<MediaFile>>.Fail("not implemented", 500));
        public bool CanSeeLibrary(User user, MediaLibrary library) => true;
        public bool CanSeeItem(User user, MediaItem item)
            => !item.DirectoryPath.StartsWith("/secret", StringComparison.Ordinal);
        public Task<bool> CanSeeItemAsync(User user, MediaItem item, CancellationToken ct = default) => Task.FromResult(CanSeeItem(user, item));
    }
}
