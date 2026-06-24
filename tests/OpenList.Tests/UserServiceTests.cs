using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// UserService tests against in-memory SQLite. Verifies CRUD, password hashing, 2FA cancel.
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
    public async Task CreateAsync_Persists_User_And_Hashes_Password()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx);
        var r = await svc.CreateAsync(new CreateUserReq
        {
            Username = "alice",
            Password = "pw",
            Role = UserRole.Normal,
        });
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Username).IsEqualTo("alice");
        await Assert.That(r.Data.Role).IsEqualTo(UserRole.Normal);
        await Assert.That(UserService.VerifyPassword("pw", ctx.Users.First().PasswordHash)).IsTrue();
    }

    [Test]
    public async Task CreateAsync_Rejects_Duplicate_Username()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx);
        await svc.CreateAsync(new CreateUserReq { Username = "bob", Password = "pw" });
        var dup = await svc.CreateAsync(new CreateUserReq { Username = "bob", Password = "pw" });
        await Assert.That(dup.Code).IsEqualTo(409);
    }

    [Test]
    public async Task GetByNameAsync_Returns_User()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx);
        await svc.CreateAsync(new CreateUserReq { Username = "carol", Password = "pw" });
        var r = await svc.GetByNameAsync("carol");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Username).IsEqualTo("carol");
    }

    [Test]
    public async Task UpdateAsync_Updates_Password_And_Role()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx);
        var created = await svc.CreateAsync(new CreateUserReq { Username = "dave", Password = "pw" });
        var r = await svc.UpdateAsync(created.Data!.Id, new UpdateUserReq
        {
            Password = "newpw",
            Role = UserRole.Admin,
        });
        await Assert.That(r.Code).IsEqualTo(200);
        var u = (await svc.GetAsync(created.Data.Id)).Data!;
        await Assert.That(u.Role).IsEqualTo(UserRole.Admin);
        await Assert.That(UserService.VerifyPassword("newpw", u.PasswordHash)).IsTrue();
    }

    [Test]
    public async Task DeleteAsync_Refuses_Admin()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx);
        var created = await svc.CreateAsync(new CreateUserReq
        {
            Username = "admin2",
            Password = "pw",
            Role = UserRole.Admin,
        });
        var r = await svc.DeleteAsync(created.Data!.Id);
        await Assert.That(r.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Cancel2FaAsync_Clears_State()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx);
        var created = await svc.CreateAsync(new CreateUserReq { Username = "eve", Password = "pw" });
        await svc.UpdateAsync(created.Data!.Id, new UpdateUserReq
        {
            TwoFactorEnabled = true,
        });
        var r = await svc.Cancel2FaAsync(created.Data.Id);
        await Assert.That(r.Code).IsEqualTo(200);
        var u = (await svc.GetAsync(created.Data.Id)).Data!;
        await Assert.That(u.TwoFactorEnabled).IsFalse();
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
