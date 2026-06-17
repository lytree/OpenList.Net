using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Core.Models;
using OpenList.Drivers.Local;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

public class DriverFactoryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DriverFactory _factory;

    public DriverFactoryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"factory_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _factory = new DriverFactory();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    [Test]
    public async Task ListDriverNames_ShouldIncludeLocal()
    {
        var names = _factory.ListDriverNames();
        await Assert.That(names).Contains("Local");
    }

    [Test]
    public async Task GetDriverInfo_Local_ShouldReturnConfig()
    {
        var info = _factory.GetDriverInfo("Local");
        await Assert.That(info).IsNotNull();
        await Assert.That(info.Config.Name).IsEqualTo("Local");
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
        await Assert.That(DriverFactory.GetActualMountPath("/data.balance")).IsEqualTo("/data");
        await Assert.That(DriverFactory.GetActualMountPath("/data.balance/sub.balance")).IsEqualTo("/data.balance/sub");
        await Assert.That(DriverFactory.GetActualMountPath("/data")).IsEqualTo("/data");
    }

    [Test]
    public async Task LoadDriver_ShouldStoreDriver()
    {
        var addition = new LocalAddition { RootFolderPath = _tempRoot };
        var storage = new Storage
        {
            MountPath = "/test_load",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        };

        _factory.LoadDriver(storage);

        await Assert.That(_factory.HasStorage("/test_load")).IsTrue();
        var driver = _factory.GetDriver("/test_load");
        await Assert.That(driver).IsNotNull();
    }

    [Test]
    public async Task UnloadDriver_ShouldRemoveDriver()
    {
        var addition = new LocalAddition { RootFolderPath = _tempRoot };
        var storage = new Storage
        {
            MountPath = "/test_unload",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        };

        _factory.LoadDriver(storage);
        await Assert.That(_factory.HasStorage("/test_unload")).IsTrue();

        _factory.UnloadDriver("/test_unload");
        await Assert.That(_factory.HasStorage("/test_unload")).IsFalse();
    }

    [Test]
    public async Task ReloadDriver_ShouldReinitialize()
    {
        var addition = new LocalAddition { RootFolderPath = _tempRoot };
        var storage = new Storage
        {
            MountPath = "/test_reload",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        };

        _factory.LoadDriver(storage);

        var newStorage = new Storage
        {
            MountPath = "/test_reload",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        };

        _factory.ReloadDriver(storage, newStorage);
        await Assert.That(_factory.HasStorage("/test_reload")).IsTrue();
    }

    [Test]
    public async Task FindStorageByPath_ShouldFindLongestMatch()
    {
        var addition = new LocalAddition { RootFolderPath = _tempRoot };
        var storage1 = new Storage
        {
            MountPath = "/root",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        };
        var storage2 = new Storage
        {
            MountPath = "/root/sub",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
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
        var addition = new LocalAddition { RootFolderPath = _tempRoot };
        var storage = new Storage
        {
            MountPath = "/virtual_test",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        };
        _factory.LoadDriver(storage);

        var files = _factory.GetVirtualFilesByPath("/");
        await Assert.That(files.Any(f => f.GetName() == "virtual_test")).IsTrue();
    }

    [Test]
    public async Task SerializeAddition_ShouldReturnJson()
    {
        var addition = new LocalAddition { RootFolderPath = _tempRoot };
        var storage = new Storage
        {
            MountPath = "/test_serialize",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        };

        _factory.LoadDriver(storage);
        var driver = _factory.GetDriver("/test_serialize");

        var json = _factory.SerializeAddition(driver!);
        await Assert.That(json).IsNotNull();
        await Assert.That(json).Contains("RootFolderPath");
    }

    [Test]
    public async Task GetAllStorages_ShouldReturnAll()
    {
        var addition = new LocalAddition { RootFolderPath = _tempRoot };
        _factory.LoadDriver(new Storage
        {
            MountPath = "/all_test_1",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        });
        _factory.LoadDriver(new Storage
        {
            MountPath = "/all_test_2",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
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
