using OpenList.Core.Drivers;
using OpenList.Core.Models;

namespace OpenList.Tests;

public class DriverBaseTests
{
    // ── DriverConfig Tests ──

    [Test]
    public async Task DriverConfig_MustProxy_OnlyProxy()
    {
        var config = new DriverConfig { OnlyProxy = true };
        await Assert.That(config.MustProxy()).IsTrue();
    }

    [Test]
    public async Task DriverConfig_MustProxy_NoLinkURL()
    {
        var config = new DriverConfig { NoLinkURL = true };
        await Assert.That(config.MustProxy()).IsTrue();
    }

    [Test]
    public async Task DriverConfig_MustProxy_NeitherSet()
    {
        var config = new DriverConfig();
        await Assert.That(config.MustProxy()).IsFalse();
    }

    [Test]
    public async Task DriverConfig_DefaultProxy_PreferProxy()
    {
        var config = new DriverConfig { PreferProxy = true };
        await Assert.That(config.DefaultProxy()).IsTrue();
    }

    [Test]
    public async Task DriverConfig_DefaultProxy_NotSet()
    {
        var config = new DriverConfig();
        await Assert.That(config.DefaultProxy()).IsFalse();
    }

    // ── Storage model tests ──

    [Test]
    public async Task Storage_Webdav302_ShouldCheckPolicy()
    {
        var s = new Storage { WebdavPolicy = "302_redirect" };
        await Assert.That(s.Webdav302()).IsTrue();
        await Assert.That(s.WebdavProxyURL()).IsFalse();
    }

    [Test]
    public async Task Storage_WebdavProxyURL_ShouldCheckPolicy()
    {
        var s = new Storage { WebdavPolicy = "use_proxy_url" };
        await Assert.That(s.Webdav302()).IsFalse();
        await Assert.That(s.WebdavProxyURL()).IsTrue();
    }
}
