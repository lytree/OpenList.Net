using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// MessageService tests against in-memory SQLite.
/// </summary>
public class MessageServiceTests
{
    [Test]
    public async Task SendAsync_Persists_Message()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        var r = await svc.SendAsync(1, "Hello", "World");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(await ctx.Messages.CountAsync()).IsEqualTo(1);
        var msg = await ctx.Messages.FirstAsync();
        await Assert.That(msg.Title).IsEqualTo("Hello");
        await Assert.That(msg.Content).IsEqualTo("World");
        await Assert.That(msg.Read).IsFalse();
    }

    [Test]
    public async Task SendAsync_Rejects_Empty_Title_Or_Content()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        var r1 = await svc.SendAsync(1, "", "content");
        await Assert.That(r1.Code).IsEqualTo(400);
        var r2 = await svc.SendAsync(1, "title", "");
        await Assert.That(r2.Code).IsEqualTo(400);
    }

    [Test]
    public async Task GetAsync_Returns_Messages_For_User()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        await svc.SendAsync(1, "M1", "C1");
        await svc.SendAsync(2, "M2", "C2");
        await svc.SendAsync(1, "M3", "C3");
        var r = await svc.GetAsync(1);
        await Assert.That(r.Data!.Count).IsEqualTo(2);
        await Assert.That(r.Data.All(m => m.UserId == 1)).IsTrue();
    }

    [Test]
    public async Task GetAsync_Orders_By_CreatedAt_Desc()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        await svc.SendAsync(1, "First", "C");
        await Task.Delay(20);
        await svc.SendAsync(1, "Second", "C");
        var r = await svc.GetAsync(1);
        await Assert.That(r.Data![0].Title).IsEqualTo("Second");
        await Assert.That(r.Data[1].Title).IsEqualTo("First");
    }

    [Test]
    public async Task GetAsync_Filters_Unread_Only()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        await svc.SendAsync(1, "Unread", "C");
        await svc.SendAsync(1, "WillRead", "C");
        var all = await svc.GetAsync(1);
        var toMark = all.Data!.First(m => m.Title == "WillRead");
        await svc.MarkReadAsync(toMark.Id);
        var unread = await svc.GetAsync(1, unreadOnly: true);
        await Assert.That(unread.Data!.Count).IsEqualTo(1);
        await Assert.That(unread.Data[0].Title).IsEqualTo("Unread");
    }

    [Test]
    public async Task MarkReadAsync_Sets_Read_True()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        await svc.SendAsync(1, "M", "C");
        var msg = (await svc.GetAsync(1)).Data![0];
        var r = await svc.MarkReadAsync(msg.Id);
        await Assert.That(r.Code).IsEqualTo(200);
        var updated = (await svc.GetAsync(1)).Data![0];
        await Assert.That(updated.Read).IsTrue();
    }

    [Test]
    public async Task MarkReadAsync_Returns_NotFound_For_Missing()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        var r = await svc.MarkReadAsync(999);
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task GetAsync_Returns_Empty_For_User_With_No_Messages()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        var r = await svc.GetAsync(42);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!).IsEmpty();
    }

    [Test]
    public async Task MarkReadAsync_Rejects_Other_Users_Message_And_Keeps_It_Unread()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        await svc.SendAsync(1, "For user 1", "C");
        var msg = (await svc.GetAsync(1)).Data![0];

        // 用户端 /me/message/read 会带上当前用户 id：别人的消息必须动不了。
        // 原先没有这道校验，猜到自增 id 就能清掉对方的未读徽标。
        var r = await svc.MarkReadAsync(msg.Id, ownerUserId: 2);

        await Assert.That(r.Code).IsEqualTo(404);
        await Assert.That((await svc.GetAsync(1)).Data![0].Read).IsFalse();
    }

    [Test]
    public async Task MarkReadAsync_Without_Owner_Keeps_Admin_Path_Working()
    {
        await using var ctx = BuildContext();
        var svc = new MessageService(ctx);
        await svc.SendAsync(7, "For user 7", "C");
        var msg = (await svc.GetAsync(7)).Data![0];

        // 管理端 /admin/message/update 不传 owner，保持跨用户能力
        var r = await svc.MarkReadAsync(msg.Id);

        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That((await svc.GetAsync(7)).Data![0].Read).IsTrue();
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
}
