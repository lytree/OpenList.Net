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
/// 批量取个人状态（`POST /media/items/states`）。
///
/// 这个端点存在的唯一理由是"不要 N 次请求"，所以最该钉住的是**一次查询就够**
/// 之外的语义：不可见条目静默剔除（不能泄露存在性）、缺状态行补默认、
/// 顺序按请求（前端直接拿来渲染）、超量直接拒（不是截断）。
/// </summary>
public class MediaBatchStatesTests
{
    private static readonly User AdminUser = new() { Id = 1, Username = "admin", Role = UserRole.Admin };

    [Test]
    public async Task Returns_Only_Visible_Items_In_Request_Order()
    {
        using var ctx = BuildContext();
        var (a, b, c) = await SeedItemsAsync(ctx);

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());
        var resp = await svc.GetStatesAsync([b.Id, a.Id, c.Id], AdminUser);

        await Assert.That(resp.Code).IsEqualTo(200);
        var ids = resp.Data!.Select(s => s.ItemId).ToList();
        // 顺序跟请求走；`/secret` 下的 c 被剔除
        await Assert.That(ids.Count).IsEqualTo(2);
        await Assert.That(ids[0]).IsEqualTo(b.Id);
        await Assert.That(ids[1]).IsEqualTo(a.Id);
    }

    [Test]
    public async Task Missing_State_Rows_Default_To_Empty()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());
        var resp = await svc.GetStatesAsync([a.Id], AdminUser);

        var state = resp.Data!.Single();
        await Assert.That(state.ItemId).IsEqualTo(a.Id);
        await Assert.That(state.UserId).IsEqualTo(AdminUser.Id);
        await Assert.That(state.Watched).IsFalse();
        await Assert.That(state.Favorite).IsFalse();
        await Assert.That(state.ProgressSeconds).IsEqualTo(0L);
    }

    [Test]
    public async Task Reads_Existing_State()
    {
        using var ctx = BuildContext();
        var (a, b, _) = await SeedItemsAsync(ctx);
        ctx.UserMediaStates.Add(new UserMediaState
        {
            UserId = AdminUser.Id, ItemId = b.Id, Watched = true, Favorite = true,
            ProgressSeconds = 42, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());
        var resp = await svc.GetStatesAsync([a.Id, b.Id], AdminUser);

        var rows = resp.Data!.ToDictionary(s => s.ItemId);
        await Assert.That(rows[a.Id].Watched).IsFalse();
        await Assert.That(rows[b.Id].Watched).IsTrue();
        await Assert.That(rows[b.Id].Favorite).IsTrue();
        await Assert.That(rows[b.Id].ProgressSeconds).IsEqualTo(42L);
    }

    [Test]
    public async Task Dedupes_Repeated_Ids()
    {
        using var ctx = BuildContext();
        var (a, _, _) = await SeedItemsAsync(ctx);

        var svc = new UserMediaStateService(ctx, new FakeLibraryService());
        var resp = await svc.GetStatesAsync([a.Id, a.Id, a.Id], AdminUser);

        await Assert.That(resp.Data!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Empty_Input_Is_Ok_And_Empty()
    {
        using var ctx = BuildContext();
        var svc = new UserMediaStateService(ctx, new FakeLibraryService());

        var resp = await svc.GetStatesAsync([], AdminUser);

        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(resp.Data!.Count).IsEqualTo(0);
    }

    /// <summary>超上限是**拒绝**而不是截断：静默少返回一半，前端只会表现为"角标随机缺失"。</summary>
    [Test]
    public async Task Rejects_Oversized_Batch()
    {
        using var ctx = BuildContext();
        var svc = new UserMediaStateService(ctx, new FakeLibraryService());

        var ids = Enumerable.Range(1, BatchStatesReq.MaxItems + 1).Select(i => (long)i).ToList();
        var resp = await svc.GetStatesAsync(ids, AdminUser);

        await Assert.That(resp.Code).IsEqualTo(400);
    }

    // ------------------------------------------------------------------
    // 装置
    // ------------------------------------------------------------------

    private static async Task<(MediaItem A, MediaItem B, MediaItem Hidden)> SeedItemsAsync(OpenListDbContext ctx)
    {
        var now = DateTimeOffset.UtcNow;
        var a = new MediaItem { LibraryId = 1, Title = "A", DirectoryPath = "/visible/a", CreatedAt = now, UpdatedAt = now };
        var b = new MediaItem { LibraryId = 1, Title = "B", DirectoryPath = "/visible/b", CreatedAt = now, UpdatedAt = now };
        var hidden = new MediaItem { LibraryId = 1, Title = "C", DirectoryPath = "/secret/c", CreatedAt = now, UpdatedAt = now };
        ctx.MediaItems.AddRange(a, b, hidden);
        await ctx.SaveChangesAsync();
        return (a, b, hidden);
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

    /// <summary>IMediaLibraryService 假件：`/secret` 前缀不可见，其余都可见。</summary>
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
