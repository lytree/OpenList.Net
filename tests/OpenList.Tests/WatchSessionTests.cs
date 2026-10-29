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
    private static readonly User Admin = new() { Id = 1, Username = "admin", Role = UserRole.Admin };
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
        await Assert.That(back.Data.WatchedSeconds).IsEqualTo(0L);

        var tick = await Report(svc, "t1", a.Id, 70);       // 恢复播放
        await Assert.That(tick.Data.WatchedSeconds).IsEqualTo(10L);
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

    // ------------------------------------------------------------------
    // 装置
    // ------------------------------------------------------------------

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
    }
}
