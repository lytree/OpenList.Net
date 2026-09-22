using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Drivers.Local;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// StorageService tests: CRUD, enable/disable, driver discovery.
/// Uses Disabled=true to avoid actual driver mounting (tested separately).
/// </summary>
public class StorageServiceTests
{
    [Test]
    public async Task CreateAsync_Persists_Storage()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var r = await svc.CreateAsync(new CreateStorageReq
        {
            MountPath = "/local",
            Driver = "Local",
            Addition = "{}",
            Order = 0,
            Disabled = true,
        });
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.MountPath).IsEqualTo("/local");
        await Assert.That(ctx.Storages.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task CreateAsync_Rejects_Duplicate_MountPath()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        await svc.CreateAsync(new CreateStorageReq { MountPath = "/a", Driver = "Local", Disabled = true });
        var dup = await svc.CreateAsync(new CreateStorageReq { MountPath = "/a", Driver = "Local", Disabled = true });
        await Assert.That(dup.Code).IsEqualTo(409);
    }

    [Test]
    public async Task CreateAsync_Validates_MountPath_StartsWith_Slash()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var r = await svc.CreateAsync(new CreateStorageReq { MountPath = "noslash", Driver = "Local", Disabled = true });
        await Assert.That(r.Code).IsEqualTo(400);
    }

    [Test]
    public async Task ListAsync_Returns_Ordered()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        await svc.CreateAsync(new CreateStorageReq { MountPath = "/b", Driver = "Local", Order = 2, Disabled = true });
        await svc.CreateAsync(new CreateStorageReq { MountPath = "/a", Driver = "Local", Order = 1, Disabled = true });
        var r = await svc.ListAsync();
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Content).Count().IsEqualTo(2);
        await Assert.That(r.Data.Content[0].MountPath).IsEqualTo("/a");
    }

    [Test]
    public async Task UpdateAsync_Changes_Remark()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var created = await svc.CreateAsync(new CreateStorageReq { MountPath = "/x", Driver = "Local", Disabled = true });
        var r = await svc.UpdateAsync(created.Data!.Id, new CreateStorageReq
        {
            MountPath = "/x",
            Driver = "Local",
            Remark = "updated",
            Disabled = true,
        });
        await Assert.That(r.Code).IsEqualTo(200);
        var s = (await svc.GetAsync(created.Data.Id)).Data!;
        await Assert.That(s.Remark).IsEqualTo("updated");
    }

    [Test]
    public async Task DeleteAsync_Removes_Storage()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var created = await svc.CreateAsync(new CreateStorageReq { MountPath = "/del", Driver = "Local", Disabled = true });
        var r = await svc.DeleteAsync(created.Data!.Id);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(ctx.Storages.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task DisableAsync_Toggles_Disabled_Flag()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var created = await svc.CreateAsync(new CreateStorageReq
        {
            MountPath = "/toggle",
            Driver = "Local",
            Disabled = true,
        });
        await svc.DisableAsync(created.Data!.Id);
        var s = (await svc.GetAsync(created.Data.Id)).Data!;
        await Assert.That(s.Disabled).IsTrue();
    }

    [Test]
    public async Task ListDriversAsync_Returns_LocalDriver()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var r = await svc.ListDriversAsync();
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Keys).Contains("Local");
    }

    [Test]
    public async Task ListDriversAsync_LocalDriver_HasCommonFields()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var r = await svc.ListDriversAsync();
        var local = r.Data!["Local"];
        var commonNames = local.Common.Select(c => c.Name).ToList();
        await Assert.That(commonNames).Contains("mount_path");
        await Assert.That(commonNames).Contains("order");
        await Assert.That(commonNames).Contains("remark");
        await Assert.That(commonNames).Contains("cache_expiration");
        await Assert.That(commonNames).Contains("webdav_policy");
        await Assert.That(commonNames).Contains("extract_folder");
        await Assert.That(commonNames).Contains("enable_sign");
        var mountPath = local.Common.First(c => c.Name == "mount_path");
        await Assert.That(mountPath.Required).IsTrue();
        await Assert.That(mountPath.Type).IsEqualTo("string");
    }

    [Test]
    public async Task GetDriverInfoAsync_Returns_Config_Schema()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var r = await svc.GetDriverInfoAsync("Local");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Additional).Count().IsEqualTo(3);
        await Assert.That(r.Data.Additional[0].Name).IsEqualTo("root_folder_path");
        await Assert.That(r.Data.Config.Name).IsEqualTo("Local");
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

/// <summary>
/// LocalDriver tests: init, mount, basic file ops.
/// </summary>
public class LocalDriverTests
{
    [Test]
    public async Task InitAsync_Mounts_Root_Directory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenListTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create a test file in the root.
            await File.WriteAllTextAsync(Path.Combine(tempDir, "hello.txt"), "world");

            var driver = new LocalDriver();
            var addition = "{\"root_folder_path\":\"" + tempDir.Replace("\\", "\\\\") + "\"}";
            await driver.InitAsync(new Core.Drivers.DriverConfig
            {
                MountPath = "/local",
                Driver = "Local",
                Addition = addition,
            });

            await Assert.That(driver.System).IsNotNull();
            await Assert.That(driver.System!.FileExists("/hello.txt")).IsTrue();
            using (var stream = driver.System.OpenFile("/hello.txt", FileMode.Open, FileAccess.Read))
            using (var reader = new StreamReader(stream))
            {
                await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("world");
            }
            await driver.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Test]
    public async Task InitAsync_Rejects_Missing_Root()
    {
        var driver = new LocalDriver();
        Exception? caught = null;
        try
        {
            await driver.InitAsync(new Core.Drivers.DriverConfig
            {
                MountPath = "/local",
                Driver = "Local",
                Addition = "{}",
            });
        }
        catch (Exception ex) { caught = ex; }
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught is InvalidOperationException).IsTrue();
    }

    [Test]
    public async Task InitAsync_Rejects_Nonexistent_Directory()
    {
        var driver = new LocalDriver();
        var fakePath = Path.Combine(Path.GetTempPath(), "OpenListFake_" + Guid.NewGuid().ToString("N"));
        var addition = "{\"root_folder_path\":\"" + fakePath.Replace("\\", "\\\\") + "\"}";
        Exception? caught = null;
        try
        {
            await driver.InitAsync(new Core.Drivers.DriverConfig
            {
                MountPath = "/local",
                Driver = "Local",
                Addition = addition,
            });
        }
        catch (Exception ex) { caught = ex; }
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught is DirectoryNotFoundException).IsTrue();
    }

    [Test]
    public async Task DriverName_Is_Local()
    {
        var driver = new LocalDriver();
        await Assert.That(driver.DriverName).IsEqualTo("Local");
        await Assert.That(driver.ReadOnly).IsFalse();
        await Assert.That(driver.IsCloudDriver).IsFalse();
    }
}
