using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Tests;

public class ModelTests
{
    [Test]
    public async Task User_DefaultValues_ShouldBeCorrect()
    {
        var user = new User();
        await Assert.That(user.Username).IsEmpty();
        await Assert.That(user.BasePath).IsEqualTo("/");
        await Assert.That(user.Role).IsEqualTo(UserRole.General);
        await Assert.That(user.Disabled).IsFalse();
        await Assert.That(user.AllowLdap).IsTrue();
        await Assert.That(user.IsGuest).IsFalse();
        await Assert.That(user.IsAdmin).IsFalse();
    }

    [Test]
    public async Task User_IsAdmin_ShouldReturnTrueForAdminRole()
    {
        var user = new User { Role = UserRole.Admin };
        await Assert.That(user.IsAdmin).IsTrue();
    }

    [Test]
    public async Task User_IsGuest_ShouldReturnTrueForGuestRole()
    {
        var user = new User { Role = UserRole.Guest };
        await Assert.That(user.IsGuest).IsTrue();
    }

    [Test]
    public async Task User_HasPermission_ShouldCheckBitwise()
    {
        var user = new User { Permission = 3 }; // bits 0 and 1
        await Assert.That(user.HasPermission(UserPermission.CanSeeHides)).IsTrue();
        await Assert.That(user.HasPermission(UserPermission.CanAccessWithoutPassword)).IsTrue();
        await Assert.That(user.HasPermission(UserPermission.CanAddOfflineDownloadTasks)).IsFalse();
    }

    [Test]
    public async Task User_JoinPath_ShouldCombineCorrectly()
    {
        var user = new User { BasePath = "/data" };
        var result = user.JoinPath("/file.txt").Replace('\\', '/');
        await Assert.That(result).IsEqualTo("/data/file.txt");
    }

    [Test]
    public async Task User_JoinPath_RootBasePath_ShouldWork()
    {
        var user = new User { BasePath = "/" };
        var result = user.JoinPath("/file.txt").Replace('\\', '/');
        await Assert.That(result).IsEqualTo("/file.txt");
    }

    [Test]
    public async Task Resp_ShouldWrapDataCorrectly()
    {
        var resp = new Resp<string>("hello");
        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(resp.Message).IsEqualTo("success");
        await Assert.That(resp.Data).IsEqualTo("hello");
    }

    [Test]
    public async Task Resp_Error_ShouldSetErrorCodeAndMessage()
    {
        var resp = Resp<string>.Error(404, "not found");
        await Assert.That(resp.Code).IsEqualTo(404);
        await Assert.That(resp.Message).IsEqualTo("not found");
        await Assert.That(resp.Data).IsNull();
    }

    [Test]
    public async Task PageReq_DefaultValues_ShouldBeCorrect()
    {
        var req = new PageReq();
        await Assert.That(req.Page).IsEqualTo(1);
        await Assert.That(req.PerPage).IsEqualTo(int.MaxValue);
    }
}
