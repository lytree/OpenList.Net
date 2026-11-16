using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// UserService tests against in-memory SQLite. Verifies account queries, updates,
/// password hashing and the single-account bootstrap/reset paths.
/// </summary>
public class UserServiceTests
{
    [Test]
    public async Task HashPassword_Verifies_Correctly()
    {
        var hash = UserService.HashPassword("s3cret");
        await Assert.That(UserService.VerifyPassword("s3cret", hash)).IsTrue();
        await Assert.That(UserService.VerifyPassword("wrong", hash)).IsFalse();
        await Assert.That(UserService.VerifyPassword("", hash)).IsFalse();
    }

    [Test]
    public async Task GetByNameAsync_Returns_User()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        await svc.CreateInitialAdminAsync("carol", "pw");
        var r = await svc.GetByNameAsync("carol");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Username).IsEqualTo("carol");
    }

    [Test]
    public async Task UpdateAsync_Updates_Password()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        var created = await svc.CreateInitialAdminAsync("dave", "pw");
        var r = await svc.UpdateAsync(created.Data!.Id, new UpdateUserReq { Password = "newpw" });
        await Assert.That(r.Code).IsEqualTo(200);
        var u = (await svc.GetAsync(created.Data.Id)).Data!;
        await Assert.That(UserService.VerifyPassword("newpw", u.PasswordHash)).IsTrue();
    }

    [Test]
    public async Task GetAdminAsync_Returns_404_When_No_Account_Exists()
    {
        // Mirrors Go op.GetAdmin returning gorm.ErrRecordNotFound — the init wizard
        // relies on this to decide whether the system still needs setup.
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        var r = await svc.GetAdminAsync();
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task GetAdminAsync_Returns_First_By_Id()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        await svc.CreateInitialAdminAsync("first", "pw");
        var r = await svc.GetAdminAsync();
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Username).IsEqualTo("first");
    }

    [Test]
    public async Task CreateInitialAdminAsync_Rejects_Second_Creation()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        var first = await svc.CreateInitialAdminAsync("first", "pw");
        await Assert.That(first.Code).IsEqualTo(200);

        var second = await svc.CreateInitialAdminAsync("second", "pw");
        await Assert.That(second.Code).IsEqualTo(400);
        await Assert.That(await ctx.Users.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task CreateInitialAdminAsync_Hashes_Password()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        await svc.CreateInitialAdminAsync("root", "rootpw");
        var u = await ctx.Users.SingleAsync();
        await Assert.That(UserService.VerifyPassword("rootpw", u.PasswordHash)).IsTrue();
    }

    [Test]
    public async Task ResetAdminPasswordAsync_Updates_And_Rejects_Empty()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        var created = await svc.CreateInitialAdminAsync("root", "oldpw");

        var empty = await svc.ResetAdminPasswordAsync("  ");
        await Assert.That(empty.Code).IsEqualTo(400);

        var ok = await svc.ResetAdminPasswordAsync("newpw");
        await Assert.That(ok.Code).IsEqualTo(200);
        var u = (await svc.GetAsync(created.Data!.Id)).Data!;
        await Assert.That(UserService.VerifyPassword("newpw", u.PasswordHash)).IsTrue();
    }

    [Test]
    public async Task ResetAdminPasswordAsync_Returns_404_When_No_Account_Exists()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        var r = await svc.ResetAdminPasswordAsync("newpw");
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task UpdateAsync_Keeps_Tokens_When_Nothing_Relevant_Changes()
    {
        // 仅改密码（不涉及 Disabled）不应登记失效。
        await using var ctx = BuildContext();
        var cutoffs = new UserTokenCutoffRegistry();
        var svc = new UserService(ctx, null, cutoffs);
        var created = await svc.CreateInitialAdminAsync("zoe", "pw");
        var before = DateTime.UtcNow.AddMinutes(-1);

        await svc.UpdateAsync(created.Data!.Id, new UpdateUserReq { Password = "newpw" });

        await Assert.That(cutoffs.IsStale(created.Data.Id, before)).IsFalse();
    }

    [Test]
    public async Task UpdateAsync_Invalidates_Tokens_When_Disabled()
    {
        // 禁用必须让既有 token 失效，否则被禁账号还能凭旧 token 操作到过期。
        await using var ctx = BuildContext();
        var cutoffs = new UserTokenCutoffRegistry();
        var svc = new UserService(ctx, null, cutoffs);
        var created = await svc.CreateInitialAdminAsync("zoe", "pw");
        var before = DateTime.UtcNow.AddMinutes(-1);

        await svc.UpdateAsync(created.Data!.Id, new UpdateUserReq { Disabled = true });

        await Assert.That(cutoffs.IsStale(created.Data.Id, before)).IsTrue();
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

    private static UserService NewUserService(OpenListDbContext ctx) => new(ctx, null, null);
}
