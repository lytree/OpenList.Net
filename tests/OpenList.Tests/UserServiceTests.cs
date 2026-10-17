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
        var svc = NewUserService(ctx);
        var r = await svc.CreateAsync(new CreateUserReq
        {
            Username = "alice",
            Password = "pw",
            Role = UserRole.General,
        });
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Username).IsEqualTo("alice");
        await Assert.That(r.Data.Role).IsEqualTo(UserRole.General);
        await Assert.That(UserService.VerifyPassword("pw", ctx.Users.First().PasswordHash)).IsTrue();
    }

    [Test]
    public async Task CreateAsync_Rejects_Duplicate_Username()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        await svc.CreateAsync(new CreateUserReq { Username = "bob", Password = "pw" });
        var dup = await svc.CreateAsync(new CreateUserReq { Username = "bob", Password = "pw" });
        await Assert.That(dup.Code).IsEqualTo(409);
    }

    [Test]
    public async Task GetByNameAsync_Returns_User()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        await svc.CreateAsync(new CreateUserReq { Username = "carol", Password = "pw" });
        var r = await svc.GetByNameAsync("carol");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Username).IsEqualTo("carol");
    }

    [Test]
    public async Task UpdateAsync_Updates_Password_And_Role()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
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
    public async Task DeleteAsync_Allows_Non_BuiltIn_Admin()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        var created = await svc.CreateAsync(new CreateUserReq
        {
            Username = "admin2",
            Password = "pw",
            Role = UserRole.Admin,
        });
        // 保护范围收窄为「内置admin」：只保护系统唯一的管理入口，
        // 后来手工创建的管理员照常可删（见 BuiltInAdminTests 覆盖标记位语义）。
        var r = await svc.DeleteAsync(created.Data!.Id);
        await Assert.That(r.Code).IsEqualTo(200);
    }

    [Test]
    public async Task BuiltInAdmin_Cannot_Be_Demoted_Deleted_Or_Renamed()
    {
        await using var ctx = BuildContext();
        var config = new AppConfiguration { AdminUsername = "root", AdminPassword = "rootpw" };
        var svc = new UserService(ctx, config);

        // 内置管理员由 IsBuiltIn 标记识别（不再依赖与配置用户名比对）。
        var created = await svc.CreateAsync(new CreateUserReq
        {
            Username = "root",
            Password = "rootpw",
            Role = UserRole.Admin,
        });
        var user = ctx.Users.First(u => u.Id == created.Data!.Id);
        user.IsBuiltIn = true;
        await ctx.SaveChangesAsync();

        // Demotion is forbidden.
        var demote = await svc.UpdateAsync(created.Data.Id, new UpdateUserReq { Role = UserRole.General });
        await Assert.That(demote.Code).IsEqualTo(403);

        // Rename is forbidden.
        var rename = await svc.UpdateAsync(created.Data.Id, new UpdateUserReq { Username = "hacker" });
        await Assert.That(rename.Code).IsEqualTo(403);

        // Disable is forbidden.
        var disable = await svc.UpdateAsync(created.Data.Id, new UpdateUserReq { Disabled = true });
        await Assert.That(disable.Code).IsEqualTo(403);

        // Deletion is forbidden.
        var del = await svc.DeleteAsync(created.Data.Id);
        await Assert.That(del.Code).IsEqualTo(403);
    }

    [Test]
    public async Task Cancel2FaAsync_Clears_State()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
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

    [Test]
    public async Task GetAdminAsync_Returns_404_When_No_Admin_Exists()
    {
        // Mirrors Go op.GetAdmin returning gorm.ErrRecordNotFound — the init wizard
        // relies on this to decide whether the system still needs setup.
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        await svc.CreateAsync(new CreateUserReq { Username = "plain", Password = "pw", Role = UserRole.General });
        var r = await svc.GetAdminAsync();
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task GetAdminAsync_Returns_First_Admin_By_Id()
    {
        await using var ctx = BuildContext();
        var svc = NewUserService(ctx);
        await svc.CreateAsync(new CreateUserReq { Username = "first", Password = "pw", Role = UserRole.Admin, BasePath = "/", Permission = 0x71FF });
        await svc.CreateAsync(new CreateUserReq { Username = "second", Password = "pw", Role = UserRole.Admin });
        var r = await svc.GetAdminAsync();
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Username).IsEqualTo("first");
        await Assert.That(r.Data.Role).IsEqualTo(UserRole.Admin);
    }

    [Test]
    public async Task UpdateAsync_Invalidates_Tokens_When_BasePath_Changes()
    {
        // BasePath 被签进 token，改小后必须让既有 token 失效（详见 UserTokenCutoffRegistry）。
        await using var ctx = BuildContext();
        var cutoffs = new UserTokenCutoffRegistry();
        var svc = new UserService(ctx, DefaultConfig(), null, cutoffs);
        var created = await svc.CreateAsync(new CreateUserReq { Username = "zoe", Password = "pw", BasePath = "/a" });
        var before = DateTime.UtcNow.AddMinutes(-1);

        await svc.UpdateAsync(created.Data!.Id, new UpdateUserReq { BasePath = "/b" });

        await Assert.That(cutoffs.IsStale(created.Data.Id, before)).IsTrue();
    }

    [Test]
    public async Task UpdateAsync_Invalidates_Tokens_When_Permission_Changes()
    {
        await using var ctx = BuildContext();
        var cutoffs = new UserTokenCutoffRegistry();
        var svc = new UserService(ctx, DefaultConfig(), null, cutoffs);
        var created = await svc.CreateAsync(new CreateUserReq { Username = "zoe", Password = "pw", Permission = 7 });
        var before = DateTime.UtcNow.AddMinutes(-1);

        await svc.UpdateAsync(created.Data!.Id, new UpdateUserReq { Permission = 0 });

        await Assert.That(cutoffs.IsStale(created.Data.Id, before)).IsTrue();
    }

    [Test]
    public async Task UpdateAsync_Keeps_Tokens_When_Nothing_Relevant_Changes()
    {
        // 仅改密码（不涉及 Role/Permission/BasePath）不应登记失效。
        await using var ctx = BuildContext();
        var cutoffs = new UserTokenCutoffRegistry();
        var svc = new UserService(ctx, DefaultConfig(), null, cutoffs);
        var created = await svc.CreateAsync(new CreateUserReq { Username = "zoe", Password = "pw", BasePath = "/a" });
        var before = DateTime.UtcNow.AddMinutes(-1);

        await svc.UpdateAsync(created.Data!.Id, new UpdateUserReq { Password = "newpw" });

        await Assert.That(cutoffs.IsStale(created.Data.Id, before)).IsFalse();
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

    private static AppConfiguration DefaultConfig() => new();

    private static UserService NewUserService(OpenListDbContext ctx) => new(ctx, DefaultConfig());
}
