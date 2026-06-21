using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Core.Models;
using OpenList.Drivers.Virtual;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

public class ZioMountServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly ZioMountService _factory;

    public ZioMountServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"factory_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _factory = new ZioMountService();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
        try { _factory.Dispose(); } catch { }
    }

    [Test]
    public async Task ListDriverNames_ShouldIncludeVirtual()
    {
        var names = _factory.ListDriverNames();
        await Assert.That(names).Contains("Virtual");
    }

    [Test]
    public async Task GetDriverInfo_Virtual_ShouldReturnConfig()
    {
        var info = _factory.GetDriverInfo("Virtual");
        await Assert.That(info).IsNotNull();
        await Assert.That(info.Config.Name).IsEqualTo("Virtual");
        await Assert.That(info.Config.LocalSort).IsTrue();
    }

    [Test]
    public async Task GetDriverInfo_NonExistent_ShouldReturnEmptyInfo()
    {
        var info = _factory.GetDriverInfo("NonExistent");
        await Assert.That(info).IsNotNull();
        await Assert.That(info.Common).IsEmpty();
    }

    [Test]
    public async Task GetActualMountPath_ShouldStripBalance()
    {
        await Assert.That(ZioMountService.GetActualMountPath("/data.balance")).IsEqualTo("/data");
        await Assert.That(ZioMountService.GetActualMountPath("/data.balance/sub.balance")).IsEqualTo("/data.balance/sub");
        await Assert.That(ZioMountService.GetActualMountPath("/data")).IsEqualTo("/data");
    }

    [Test]
    public async Task LoadDriver_ShouldStoreDriver()
    {
        var storage = new Storage
        {
            MountPath = "/test_load",
            Driver = "Virtual",
            Addition = "{}"
        };

        _factory.LoadDriver(storage);

        await Assert.That(_factory.HasStorage("/test_load")).IsTrue();
        var driver = _factory.GetFileSystem("/test_load");
        await Assert.That(driver).IsNotNull();
    }

    [Test]
    public async Task UnloadDriver_ShouldRemoveDriver()
    {
        var storage = new Storage
        {
            MountPath = "/test_unload",
            Driver = "Virtual",
            Addition = "{}"
        };

        _factory.LoadDriver(storage);
        await Assert.That(_factory.HasStorage("/test_unload")).IsTrue();

        _factory.UnloadDriver("/test_unload");
        await Assert.That(_factory.HasStorage("/test_unload")).IsFalse();
    }

    [Test]
    public async Task ReloadDriver_ShouldReinitialize()
    {
        var storage = new Storage
        {
            MountPath = "/test_reload",
            Driver = "Virtual",
            Addition = "{}"
        };

        _factory.LoadDriver(storage);

        var newStorage = new Storage
        {
            MountPath = "/test_reload",
            Driver = "Virtual",
            Addition = "{}"
        };

        _factory.ReloadDriver(storage, newStorage);
        await Assert.That(_factory.HasStorage("/test_reload")).IsTrue();
    }

    [Test]
    public async Task FindStorageByPath_ShouldFindLongestMatch()
    {
        var storage1 = new Storage
        {
            MountPath = "/root",
            Driver = "Virtual",
            Addition = "{}"
        };
        var storage2 = new Storage
        {
            MountPath = "/root/sub",
            Driver = "Virtual",
            Addition = "{}"
        };

        _factory.LoadDriver(storage1);
        _factory.LoadDriver(storage2);

        var found = _factory.FindStorageByPath("/root/sub/file.txt");
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.MountPath).IsEqualTo("/root/sub");
    }

    [Test]
    public async Task FindStorageByPath_NoMatch_ShouldReturnNull()
    {
        var found = _factory.FindStorageByPath("/nonexistent/path");
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task GetVirtualFilesByPath_ShouldReturnVirtualDirs()
    {
        var storage = new Storage
        {
            MountPath = "/virtual_test",
            Driver = "Virtual",
            Addition = "{}"
        };
        _factory.LoadDriver(storage);

        var files = _factory.GetVirtualFilesByPath("/");
        await Assert.That(files.Any(f => f.Name == "virtual_test")).IsTrue();
    }

    [Test]
    public async Task SerializeAddition_ShouldReturnJson()
    {
        var storage = new Storage
        {
            MountPath = "/test_serialize",
            Driver = "Virtual",
            Addition = "{}"
        };

        _factory.LoadDriver(storage);
        var driver = _factory.GetFileSystem("/test_serialize");

        var json = _factory.SerializeAddition(driver!);
        await Assert.That(json).IsNotNull();
    }

    [Test]
    public async Task GetAllStorages_ShouldReturnAll()
    {
        _factory.LoadDriver(new Storage
        {
            MountPath = "/all_test_1",
            Driver = "Virtual",
            Addition = "{}"
        });
        _factory.LoadDriver(new Storage
        {
            MountPath = "/all_test_2",
            Driver = "Virtual",
            Addition = "{}"
        });

        var storages = _factory.GetAllStorages();
        await Assert.That(storages.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ListDriverInfos_ShouldReturnInfos()
    {
        var infos = _factory.ListDriverInfos();
        await Assert.That(infos).IsNotNull();
        await Assert.That(infos.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task LoadDriver_NonExistentDriver_ShouldNotCrash()
    {
        var storage = new Storage
        {
            MountPath = "/bad_driver",
            Driver = "NonExistentDriver",
            Addition = "{}"
        };

        // Should not throw
        _factory.LoadDriver(storage);
        await Assert.That(_factory.HasStorage("/bad_driver")).IsFalse();
    }
}
