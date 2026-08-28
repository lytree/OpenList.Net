using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// SettingService tests against in-memory SQLite.
/// </summary>
public class SettingServiceTests
{
    [Test]
    public async Task SaveAsync_Inserts_New_Settings()
    {
        await using var ctx = BuildContext();
        var svc = BuildService(ctx);
        var r = await svc.SaveAsync([
            new SettingItem { Key = "site_title", Value = "OpenList", Flag = SettingFlag.Public },
            new SettingItem { Key = "jwt_secret", Value = "abc", Flag = SettingFlag.Private },
        ]);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(await ctx.Settings.CountAsync()).IsEqualTo(2);
    }

    [Test]
    public async Task SaveAsync_Updates_Existing_Settings()
    {
        await using var ctx = BuildContext();
        var svc = BuildService(ctx);
        await svc.SaveAsync([new SettingItem { Key = "site_title", Value = "Old" }]);
        await svc.SaveAsync([new SettingItem { Key = "site_title", Value = "New" }]);
        var item = (await svc.GetAsync("site_title")).Data!;
        await Assert.That(item.Value).IsEqualTo("New");
    }

    [Test]
    public async Task ListAsync_Returns_All_Settings()
    {
        await using var ctx = BuildContext();
        var svc = BuildService(ctx);
        await svc.SaveAsync([
            new SettingItem { Key = "b", Value = "2" },
            new SettingItem { Key = "a", Value = "1" },
        ]);
        var r = await svc.ListAsync();
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Count).IsEqualTo(2);
        await Assert.That(r.Data[0].Key).IsEqualTo("a"); // ordered by key
    }

    [Test]
    public async Task GetAsync_Returns_NotFound_For_Missing()
    {
        await using var ctx = BuildContext();
        var svc = BuildService(ctx);
        var r = await svc.GetAsync("nope");
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task DeleteAsync_Removes_Setting()
    {
        await using var ctx = BuildContext();
        var svc = BuildService(ctx);
        await svc.SaveAsync([new SettingItem { Key = "temp", Value = "x" }]);
        var r = await svc.DeleteAsync("temp");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(await ctx.Settings.AnyAsync(s => s.Key == "temp")).IsFalse();
    }

    [Test]
    public async Task GetPublicAsync_Returns_Only_Public_Settings()
    {
        await using var ctx = BuildContext();
        var svc = BuildService(ctx);
        await svc.SaveAsync([
            new SettingItem { Key = "pub1", Value = "1", Flag = SettingFlag.Public },
            new SettingItem { Key = "priv1", Value = "2", Flag = SettingFlag.Private },
            new SettingItem { Key = "pub2", Value = "3", Flag = SettingFlag.Public },
        ]);
        var r = await svc.GetPublicAsync();
        await Assert.That(r.Data!.Count).IsEqualTo(2);
        await Assert.That(r.Data["pub1"]).IsEqualTo("1");
        await Assert.That(r.Data["pub2"]).IsEqualTo("3");
        await Assert.That(r.Data.ContainsKey("priv1")).IsFalse();
    }

    [Test]
    public async Task GetArchiveExtensionsAsync_Returns_Supported_Formats()
    {
        await using var ctx = BuildContext();
        var svc = BuildService(ctx);
        var r = await svc.GetArchiveExtensionsAsync();
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!).Contains(".zip");
        await Assert.That(r.Data).Contains(".tar");
        await Assert.That(r.Data).Contains(".7z");
    }

    [Test]
    public async Task GetOfflineDownloadToolsAsync_Returns_Empty_List()
    {
        await using var ctx = BuildContext();
        var svc = BuildService(ctx);
        var r = await svc.GetOfflineDownloadToolsAsync();
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!).IsEmpty();
    }

    [Test]
    public async Task DefaultSettings_Seeds_Multipart_And_Move_Task_Settings()
    {
        // 镜像 Go 的 settings 默认值：multipart_enabled / multipart_chunk_size / move_task_threads_num。
        var byKey = DbInitializer.DefaultSettings.ToDictionary(d => d.Key, StringComparer.Ordinal);

        await Assert.That(byKey).ContainsKey("multipart_enabled");
        await Assert.That(byKey["multipart_enabled"].Value).IsEqualTo("true");
        await Assert.That(byKey["multipart_enabled"].Type).IsEqualTo(SettingValueType.Bool);

        await Assert.That(byKey).ContainsKey("multipart_chunk_size");
        await Assert.That(byKey["multipart_chunk_size"].Value).IsEqualTo("10"); // MB

        await Assert.That(byKey).ContainsKey("move_task_threads_num");
        await Assert.That(byKey["move_task_threads_num"].Value).IsEqualTo("5");
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

    private static AppConfiguration BuildAppConfig() => new()
    {
        JwtSecret = "testsecret_testsecret_testsecret_",
        Database = new DatabaseConfig(),
    };

    private static SettingService BuildService(OpenListDbContext ctx)
        => new(ctx, BuildAppConfig());
}
