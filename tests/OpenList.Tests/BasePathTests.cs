using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Vfs;

namespace OpenList.Tests;

/// <summary>
/// BasePath（用户级目录隔离）落地测试：<see cref="UPath.TryJoinBasePath"/> /
/// <see cref="User.TryJoinPath"/> 的拼接与「..」越界守卫（镜像 Go <c>utils.JoinBasePath</c>），
/// 以及 <see cref="UserTokenCutoffRegistry"/> 的失效登记语义（无状态 JWT 快速路径 +
/// 凭据变更即时失效的组合方案）。
/// </summary>
public class BasePathTests
{
    // ---------------- UPath.TryJoinBasePath ----------------

    [Test]
    public async Task TryJoinBasePath_Null_BasePath_Is_Noop()
    {
        await Assert.That(UPath.TryJoinBasePath(null, "/a/b", out var r)).IsTrue();
        await Assert.That(r).IsEqualTo("/a/b");
    }

    [Test]
    public async Task TryJoinBasePath_Root_BasePath_Is_Noop()
    {
        await Assert.That(UPath.TryJoinBasePath("/", "/a/b", out var r)).IsTrue();
        await Assert.That(r).IsEqualTo("/a/b");
    }

    [Test]
    public async Task TryJoinBasePath_Prepends_BasePath()
    {
        await Assert.That(UPath.TryJoinBasePath("/user", "/a/b", out var r)).IsTrue();
        await Assert.That(r).IsEqualTo("/user/a/b");
    }

    [Test]
    public async Task TryJoinBasePath_Root_Request_Becomes_BasePath()
    {
        await Assert.That(UPath.TryJoinBasePath("/user", "/", out var r)).IsTrue();
        await Assert.That(r).IsEqualTo("/user");
    }

    [Test]
    public async Task TryJoinBasePath_Relative_Request_Is_Normalized()
    {
        await Assert.That(UPath.TryJoinBasePath("/user", "a/b", out var r)).IsTrue();
        await Assert.That(r).IsEqualTo("/user/a/b");
    }

    [Test]
    public async Task TryJoinBasePath_Rejects_PathTraversal()
    {
        // 含 ".." 的请求路径被清洗后不再含 ".." = 试图逃出 BasePath，一律拒绝
        // （镜像 Go utils.JoinBasePath → errs.RelativePath）。
        foreach (var bad in new[] { "../secret", "/a/../b", @"..\..\secret", "..", "a/../../b" })
        {
            await Assert.That(UPath.TryJoinBasePath("/user", bad, out var r)).IsFalse();
            await Assert.That(r).IsEqualTo(string.Empty);
        }
    }

    // ---------------- User.TryJoinPath ----------------

    [Test]
    public async Task User_TryJoinPath_Uses_BasePath()
    {
        var u = new User { Id = 1, Username = "bob", BasePath = "/home/bob" };
        await Assert.That(u.TryJoinPath("/docs/a.txt", out var r)).IsTrue();
        await Assert.That(r).IsEqualTo("/home/bob/docs/a.txt");
    }

    [Test]
    public async Task User_TryJoinPath_Null_BasePath_Is_Noop()
    {
        var u = new User { Id = 1, Username = "admin" };
        await Assert.That(u.TryJoinPath("/docs/a.txt", out var r)).IsTrue();
        await Assert.That(r).IsEqualTo("/docs/a.txt");
    }

    [Test]
    public async Task User_TryJoinPath_Rejects_PathTraversal()
    {
        var u = new User { Id = 1, Username = "bob", BasePath = "/home/bob" };
        await Assert.That(u.TryJoinPath("../../etc/passwd", out _)).IsFalse();
    }

    // ---------------- UserTokenCutoffRegistry ----------------

    [Test]
    public async Task CutoffRegistry_IsStale_True_For_Tokens_Before_Cutoff()
    {
        var reg = new UserTokenCutoffRegistry();
        reg.InvalidateUser(42, DateTimeOffset.UtcNow);

        await Assert.That(reg.IsStale(42, DateTime.UtcNow.AddMinutes(-1))).IsTrue();
    }

    [Test]
    public async Task CutoffRegistry_IsStale_False_For_Unknown_User()
    {
        var reg = new UserTokenCutoffRegistry();
        await Assert.That(reg.IsStale(99, DateTime.UtcNow.AddDays(-1))).IsFalse();
    }

    [Test]
    public async Task CutoffRegistry_IsStale_Uses_Second_Precision_FailClosed()
    {
        // JWT 的 nbf 只有秒级精度：截止点取整秒 +1s。变更所在整秒签发的 token（nbf 被截断到该秒）
        // 仍视为失效（fail-closed）；下一整秒起视为有效。
        var reg = new UserTokenCutoffRegistry();
        reg.InvalidateUser(7, new DateTimeOffset(2026, 1, 2, 3, 4, 5, 600, TimeSpan.Zero));

        await Assert.That(reg.IsStale(7, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))).IsTrue();
        await Assert.That(reg.IsStale(7, new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc))).IsFalse();
        await Assert.That(reg.IsStale(7, new DateTime(2026, 1, 2, 3, 4, 7, DateTimeKind.Utc))).IsFalse();
    }
}
