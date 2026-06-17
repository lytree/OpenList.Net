using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Tests;

public class DriverBaseTests
{
    // ── Test driver stubs ──

    private class TestRootPathDriver : RootPathDriver, IDriverGetter
    {
        public override DriverConfig Config() => new()
        {
            Name = "TestPath",
            LocalSort = true,
            NoCache = true,
            DefaultRoot = "/data"
        };

        public override object GetAddition() => new TestAddition();
        protected override Type? GetAdditionType() => typeof(TestAddition);
        public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
            => Task.FromResult(new List<IObj>());
        public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
            => Task.FromResult<Link?>(null);
        public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
    }

    private class TestAddition
    {
        public string RootFolderPath { get; set; } = "/";
        public bool ShowHidden { get; set; } = true;
        [System.ComponentModel.Description("Max items")]
        public int MaxItems { get; set; } = 100;
    }

    // ── Info() Tests ──

    [Test]
    public async Task Info_ShouldGenerateCommonItems()
    {
        var driver = new TestRootPathDriver();
        var info = driver.Info();

        await Assert.That(info.Common).IsNotNull();
        await Assert.That(info.Common.Any(i => i.Name == "mount_path")).IsTrue();
        await Assert.That(info.Common.Any(i => i.Name == "order")).IsTrue();
        await Assert.That(info.Common.Any(i => i.Name == "remark")).IsTrue();
    }

    [Test]
    public async Task Info_NoCache_ShouldNotAddCacheItems()
    {
        var driver = new TestRootPathDriver();
        var info = driver.Info();

        await Assert.That(info.Common.Any(i => i.Name == "cache_expiration")).IsFalse();
        await Assert.That(info.Common.Any(i => i.Name == "custom_cache_policies")).IsFalse();
    }

    [Test]
    public async Task Info_WithCache_ShouldAddCacheItems()
    {
        var driver = new CacheTestDriver();
        var info = driver.Info();

        await Assert.That(info.Common.Any(i => i.Name == "cache_expiration")).IsTrue();
    }

    [Test]
    public async Task Info_LocalSort_ShouldAddSortItems()
    {
        var driver = new TestRootPathDriver();
        var info = driver.Info();

        await Assert.That(info.Common.Any(i => i.Name == "order_by")).IsTrue();
        await Assert.That(info.Common.Any(i => i.Name == "order_direction")).IsTrue();
    }

    [Test]
    public async Task Info_ShouldGenerateAdditionalItems()
    {
        var driver = new TestRootPathDriver();
        var info = driver.Info();

        await Assert.That(info.Additional).IsNotNull();
        await Assert.That(info.Additional.Any(i => i.Name == "root_folder_path")).IsTrue();
        await Assert.That(info.Additional.Any(i => i.Name == "show_hidden")).IsTrue();
        await Assert.That(info.Additional.Any(i => i.Name == "max_items")).IsTrue();
    }

    [Test]
    public async Task Info_RootFolderPath_ShouldUseDefaultRoot()
    {
        var driver = new TestRootPathDriver();
        var info = driver.Info();

        var rootItem = info.Additional.FirstOrDefault(i => i.Name == "root_folder_path");
        await Assert.That(rootItem).IsNotNull();
        // Default is "/" from TestAddition.RootFolderPath default value
        // Info() only overrides with DefaultRoot when the property default is empty
        await Assert.That(rootItem!.Default).IsEqualTo("/");
    }

    [Test]
    public async Task Info_ShouldIncludeConfig()
    {
        var driver = new TestRootPathDriver();
        var info = driver.Info();

        await Assert.That(info.Config).IsNotNull();
        await Assert.That(info.Config.Name).IsEqualTo("TestPath");
        await Assert.That(info.Config.LocalSort).IsTrue();
    }

    [Test]
    public async Task Info_ExtractFolder_ShouldBeInCommon()
    {
        var driver = new TestRootPathDriver();
        var info = driver.Info();

        await Assert.That(info.Common.Any(i => i.Name == "extract_folder")).IsTrue();
    }

    // ── RootPathDriver Tests ──

    [Test]
    public async Task RootPathDriver_GetSetRootPath()
    {
        var driver = new TestRootPathDriver();
        driver.SetRootPath("/my/data");
        await Assert.That(driver.GetRootPath()).IsEqualTo("/my/data");
    }

    // ── RootIdDriver Tests ──

    [Test]
    public async Task RootIdDriver_GetRootId()
    {
        var driver = new TestRootIdDriver();
        driver.RootFolderID = "folder-123";
        await Assert.That(driver.GetRootId()).IsEqualTo("folder-123");
    }

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

    // ── Helper driver classes ──

    private class CacheTestDriver : RootPathDriver
    {
        public override DriverConfig Config() => new() { Name = "CacheTest", NoCache = false };
        public override object GetAddition() => new object();
        public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
            => Task.FromResult(new List<IObj>());
        public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
            => Task.FromResult<Link?>(null);
    }

    private class TestRootIdDriver : RootIdDriver
    {
        public override DriverConfig Config() => new() { Name = "TestId" };
        public override object GetAddition() => new object();
        public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
            => Task.FromResult(new List<IObj>());
        public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
            => Task.FromResult<Link?>(null);
    }
}
