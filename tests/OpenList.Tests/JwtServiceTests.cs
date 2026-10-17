using System.IdentityModel.Tokens.Jwt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// JwtService tests: token issue/validate, login flow, 2FA gate, logout revocation.
/// </summary>
public class JwtServiceTests
{
    private static readonly string Secret = new string('a', 32);

    [Test]
    public async Task IssueToken_ValidateToken_RoundTrip()
    {
        var (svc, _) = await BuildAsync();
        var user = new User { Id = 42, Username = "alice", Role = UserRole.Admin };
        var token = svc.IssueToken(user);
        await Assert.That(token).IsNotNull();
        var id = await svc.ValidateTokenAsync(token);
        await Assert.That(id).IsEqualTo(42L);
    }

    [Test]
    public async Task LoginAsync_Returns_Token_For_Correct_Password()
    {
        var (svc, _) = await BuildAsync();
        var r = await svc.LoginAsync("alice", "pw");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data).IsNotNull();
    }

    [Test]
    public async Task LoginAsync_Rejects_Wrong_Password()
    {
        var (svc, _) = await BuildAsync();
        var r = await svc.LoginAsync("alice", "wrong");
        await Assert.That(r.Code).IsEqualTo(401);
    }

    [Test]
    public async Task LoginAsync_Rejects_Disabled_User()
    {
        var (svc, db) = await BuildAsync();
        db.Users.First().Disabled = true;
        await db.SaveChangesAsync();
        var r = await svc.LoginAsync("alice", "pw");
        await Assert.That(r.Code).IsEqualTo(403);
    }

    [Test]
    public async Task LogoutAsync_Revokes_Token()
    {
        var (svc, _) = await BuildAsync();
        var token = svc.IssueToken(new User { Id = 1, Username = "alice", Role = UserRole.General });
        await svc.LogoutAsync(token);
        var id = await svc.ValidateTokenAsync(token);
        await Assert.That(id).IsNull();
    }

    [Test]
    public async Task ValidateTokenAsync_Returns_Null_For_Garbage()
    {
        var (svc, _) = await BuildAsync();
        var id = await svc.ValidateTokenAsync("not.a.jwt");
        await Assert.That(id).IsNull();
    }

    [Test]
    public async Task IssueToken_Includes_BasePath_And_Permission_Claims()
    {
        // BasePath 与 Permission 是每请求的权限输入，必须随身份签进 token
        //（.NET 侧刻意保持无状态 JWT，不每请求查库）。
        var (svc, _) = await BuildAsync();
        var user = new User { Id = 7, Username = "bob", Role = UserRole.General, BasePath = "/home/bob", Permission = 0x71FF };
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(svc.IssueToken(user));

        await Assert.That(jwt.Claims.First(c => c.Type == OpenListClaims.BasePath).Value).IsEqualTo("/home/bob");
        await Assert.That(jwt.Claims.First(c => c.Type == OpenListClaims.Permission).Value).IsEqualTo(0x71FF.ToString());
    }

    [Test]
    public async Task IssueToken_Normalizes_Null_BasePath_To_Root()
    {
        // Go 的 JoinBasePath 对 null/空 BasePath 按 "/" 处理；签进 token 的值统一成同一形式。
        var (svc, _) = await BuildAsync();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(
            svc.IssueToken(new User { Id = 8, Username = "root", Role = UserRole.Admin }));

        await Assert.That(jwt.Claims.First(c => c.Type == OpenListClaims.BasePath).Value).IsEqualTo("/");
    }

    [Test]
    public async Task ValidateTokenAsync_Returns_Null_After_CutoffInvalidation()
    {
        // BasePath/权限变更后，早于截止点签发的 token 必须失效（详见 UserTokenCutoffRegistry）。
        var (svc, db, cutoffs) = await BuildWithCutoffsAsync();
        var u = db.Users.First();
        var token = svc.IssueToken(u);
        await Assert.That(await svc.ValidateTokenAsync(token)).IsEqualTo(u.Id);

        cutoffs.InvalidateUser(u.Id, DateTimeOffset.UtcNow);
        await Assert.That(await svc.ValidateTokenAsync(token)).IsNull();
    }

    private static async Task<(JwtService svc, OpenListDbContext db, UserTokenCutoffRegistry cutoffs)> BuildWithCutoffsAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var opts = new DbContextOptionsBuilder<OpenListDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new OpenListDbContext(opts, new DatabaseConfig());
        await db.Database.EnsureCreatedAsync();
        var cutoffs = new UserTokenCutoffRegistry();
        var users = new UserService(db, new AppConfiguration(), null, cutoffs);
        await users.CreateAsync(new CreateUserReq { Username = "alice", Password = "pw" });
        var config = new AppConfiguration { JwtSecret = Secret, TokenExpiresIn = 1 };
        var svc = new JwtService(users, config, null, null, cutoffs);
        return (svc, db, cutoffs);
    }

    private static async Task<(JwtService svc, OpenListDbContext db)> BuildAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var opts = new DbContextOptionsBuilder<OpenListDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new OpenListDbContext(opts, new DatabaseConfig());
        await db.Database.EnsureCreatedAsync();
        var users = new UserService(db, new AppConfiguration());
        await users.CreateAsync(new CreateUserReq { Username = "alice", Password = "pw" });
        var config = new AppConfiguration { JwtSecret = Secret, TokenExpiresIn = 1 };
        var svc = new JwtService(users, config);
        return (svc, db);
    }
}
