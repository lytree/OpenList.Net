using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using OpenList.Core.Configuration;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using OpenList.Drivers.Cloud.AliyundriveOpen;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// 阿里云盘 Open 驱动的元数据、配置 schema、装配关系与挂载语义测试。
/// <para>
/// 涉及云盘 API 的用例（初始化、列目录、直链/代理、令牌刷新）在
/// <see cref="AliyundriveOpenApiTests"/> 中，那些用例通过注入 HTTP 桩实现，同样不访问真实服务。
/// 本文件里的失败路径用例（缺 refresh_token、非法 Addition）也在发出请求之前就失败，
/// 因此整个测试套件不依赖网络。
/// </para>
/// </summary>
public class AliyundriveOpenDriverTests
{
    /// <summary>
    /// 管理端配置表单的字段清单，取自前端 <c>src/lang/en/drivers.json</c> 的
    /// <c>"AliyundriveOpen"</c> 段（由 Go 的 addition 结构反射生成）。
    /// 字段名必须与之一致，否则前端表单会缺字段或显示未翻译的原始 key。
    /// </summary>
    private static readonly string[] ExpectedFields =
    [
        "alipan_type", "api_url_address", "callback_concurrency", "client_id", "client_secret",
        "drive_type", "internal_upload", "livp_download_format", "order_by", "order_direction",
        "rapid_upload", "refresh_token", "remove_way", "root_folder_id", "use_online_api",
    ];

    [Test]
    public async Task Metadata_MatchesGoReference()
    {
        var driver = new AliyundriveOpenDriver();
        await Assert.That(driver.DriverName).IsEqualTo("AliyundriveOpen");
        await Assert.That(driver.IsCloudDriver).IsTrue();
        await Assert.That(driver.ReadOnly).IsFalse();
        await Assert.That(driver.DefaultRoot).IsEqualTo("root");
        await Assert.That(driver).IsAssignableTo<IFileSystemLinkProvider>();
        await Assert.That(driver).IsAssignableTo<IFileSystemUploader>();
    }

    [Test]
    public async Task Alert_IsInfoLevel_AndMentionsUnverifiedStatus()
    {
        var driver = new AliyundriveOpenDriver();
        // 前端按 '|' 切分 [级别]|[文案]，级别必须合法，否则 banner 不渲染。
        var parts = driver.Alert.Split('|');
        await Assert.That(parts).Count().IsEqualTo(2);
        // API 已移植，只是尚未在真实账号上验证 —— 必须是 info 级：
        // 误用 warning/danger 会让用户以为驱动不可用。
        await Assert.That(parts[0]).IsEqualTo("info");
        await Assert.That(parts[1]).Contains("已移植");
    }

    [Test]
    public async Task ConfigSchema_FieldNames_MatchFrontendTranslationKeys()
    {
        var driver = new AliyundriveOpenDriver();
        var names = driver.ConfigItems.Select(c => c.Name).ToList();

        // 数量与逐个字段都要对上：多一个字段前端没有翻译，少一个则表单缺项。
        await Assert.That(names).Count().IsEqualTo(ExpectedFields.Length);
        foreach (var expected in ExpectedFields)
            await Assert.That(names).Contains(expected);
    }

    [Test]
    public async Task ConfigSchema_RequiredFields_AreFlagged()
    {
        var driver = new AliyundriveOpenDriver();
        foreach (var required in new[] { "refresh_token", "root_folder_id", "alipan_type", "remove_way" })
        {
            var item = driver.ConfigItems.First(c => c.Name == required);
            await Assert.That(item.Required).IsTrue();
        }

        // access_token 是运行时状态，不是用户在表单里填的字段（与 Go 一致）。
        await Assert.That(driver.ConfigItems.Any(c => c.Name == "access_token")).IsFalse();
    }

    [Test]
    public async Task ConfigSchema_SelectOptions_MatchGoOptions()
    {
        var driver = new AliyundriveOpenDriver();
        await Assert.That(driver.ConfigItems.First(c => c.Name == "drive_type").Options)
            .IsEqualTo("default,resource,backup");
        await Assert.That(driver.ConfigItems.First(c => c.Name == "alipan_type").Options)
            .IsEqualTo("default,alipanTV");
        await Assert.That(driver.ConfigItems.First(c => c.Name == "remove_way").Options)
            .IsEqualTo("trash,delete");
        await Assert.That(driver.ConfigItems.First(c => c.Name == "livp_download_format").Options)
            .IsEqualTo("jpeg,mov");
    }

    [Test]
    public async Task ConfigSchema_Defaults_MatchGoDefaults()
    {
        var driver = new AliyundriveOpenDriver();
        await Assert.That(driver.ConfigItems.First(c => c.Name == "drive_type").Default).IsEqualTo("resource");
        await Assert.That(driver.ConfigItems.First(c => c.Name == "alipan_type").Default).IsEqualTo("default");
        await Assert.That(driver.ConfigItems.First(c => c.Name == "livp_download_format").Default).IsEqualTo("jpeg");
        await Assert.That(driver.ConfigItems.First(c => c.Name == "root_folder_id").Default).IsEqualTo("root");
        // Default 的静态类型是 object，无法直接套用 IsTrue()；用 `is true` 保持
        // 与 IsEqualTo(true) 等价的语义（TUnit 1.x 对后者会报 TUnitAssertions0015）。
        await Assert.That(driver.ConfigItems.First(c => c.Name == "use_online_api").Default is true)
            .IsTrue();
    }

    // 注意：初始化成功路径（getDriveInfo → 挂载文件系统 → 限流槽位切换）需要拦截 HTTP，
    // 相关用例集中在 AliyundriveOpenApiTests 里；这里只保留「配置不合法时不发请求」的语义。

    [Test]
    public async Task InitAsync_RejectsMissingRefreshToken_WithoutMounting()
    {
        var driver = new AliyundriveOpenDriver();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(new DriverConfig { Addition = "{}" }));
        await Assert.That(ex.Message).Contains("refresh_token");
        // 配置校验失败时不挂载：挂一个空文件系统会让用户误以为云盘是空的。
        await Assert.That(driver.System).IsNull();
    }

    [Test]
    public async Task Addition_ParsesAllGoFields()
    {
        // 只验证 Addition 的反序列化契约，因此直接走 DriverConfig 的解析路径：
        // 不再调用 InitAsync —— 那会真实访问云盘 API（本用例无需网络）。
        var addition = new DriverConfig
        {
            Addition = """
            {
              "drive_type": "backup",
              "root_folder_id": "fld-123",
              "refresh_token": "ref",
              "order_by": "size",
              "order_direction": "DESC",
              "use_online_api": false,
              "alipan_type": "alipanTV",
              "api_url_address": "https://example.com/renew",
              "client_id": "cid",
              "client_secret": "csec",
              "remove_way": "trash",
              "rapid_upload": true,
              "internal_upload": true,
              "livp_download_format": "mov"
            }
            """,
        }.GetAddition<AliyundriveOpenAddition>();

        await Assert.That(addition.DriveType).IsEqualTo("backup");
        await Assert.That(addition.RootFolderId).IsEqualTo("fld-123");
        await Assert.That(addition.OrderBy).IsEqualTo("size");
        await Assert.That(addition.OrderDirection).IsEqualTo("DESC");
        await Assert.That(addition.UseOnlineApi).IsFalse();
        await Assert.That(addition.AlipanType).IsEqualTo("alipanTV");
        await Assert.That(addition.ApiUrlAddress).IsEqualTo("https://example.com/renew");
        await Assert.That(addition.ClientId).IsEqualTo("cid");
        await Assert.That(addition.ClientSecret).IsEqualTo("csec");
        await Assert.That(addition.RemoveWay).IsEqualTo("trash");
        await Assert.That(addition.RapidUpload).IsTrue();
        await Assert.That(addition.InternalUpload).IsTrue();
        await Assert.That(addition.LivpDownloadFormat).IsEqualTo("mov");
    }

    [Test]
    public async Task Addition_AccessToken_RoundTrips_WithGoCompatibleKey()
    {
        // Go 的 AccessToken 字段没有 json tag，写出键名是 PascalCase 的 "AccessToken"。
        // 必须保持同样行为，才能与 Go 写出的 Addition JSON 互读。
        var json = JsonSerializer.Serialize(new AliyundriveOpenAddition
        {
            RefreshToken = "ref",
            AccessToken = "acc",
        });
        await Assert.That(json).Contains("\"AccessToken\":\"acc\"");
        await Assert.That(json).Contains("\"refresh_token\":\"ref\"");

        var back = JsonSerializer.Deserialize<AliyundriveOpenAddition>(json)!;
        await Assert.That(back.AccessToken).IsEqualTo("acc");
    }

    [Test]
    public async Task Driver_IsDiscoveredByMountService()
    {
        using var mount = new ZioMountService();
        var names = mount.DriverTypes.Keys.ToList();
        await Assert.That(names).Contains("AliyundriveOpen");
        // 原有驱动不受影响。
        await Assert.That(names).Contains("Local");
        await Assert.That(names).Contains("S3");
    }

    [Test]
    public async Task MountService_LoadAsync_InvalidConfig_PropagatesError()
    {
        using var mount = new ZioMountService();
        var storage = new Storage
        {
            Id = 1,
            MountPath = "/ali",
            Driver = "AliyundriveOpen",
            // 故意缺少 refresh_token：初始化在发出任何网络请求之前就失败，
            // 因此本用例不依赖网络。单条加载会把错误抛给调用方（区别于 LoadAllAsync 的容错）。
            Addition = "{}",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await mount.LoadAsync(storage));
        await Assert.That(mount.Mounted.ContainsKey("/ali")).IsFalse();
    }

    [Test]
    public async Task MountService_LoadAll_ContinuesAfterBrokenStorage()
    {
        using var mount = new ZioMountService();
        var storages = new[]
        {
            // 缺 refresh_token → InitAsync 在发请求之前抛 InvalidOperationException。
            new Storage { Id = 1, MountPath = "/broken", Driver = "AliyundriveOpen", Addition = "{}" },
            // Addition 不是合法 JSON → GetAddition 反序列化失败。
            new Storage { Id = 2, MountPath = "/also-broken", Driver = "AliyundriveOpen", Addition = "{not-json" },
        };

        // 单个坏存储不得中断整体加载（否则启动直接失败）。
        await mount.LoadAllAsync(storages);
        await Assert.That(mount.Mounted.ContainsKey("/broken")).IsFalse();
        await Assert.That(mount.Mounted.ContainsKey("/also-broken")).IsFalse();
    }

    [Test]
    public async Task StorageService_ListDrivers_ExposesAliyundriveOpen()
    {
        await using var ctx = BuildContext();
        var svc = new StorageService(ctx, new ZioMountService());
        var r = await svc.ListDriversAsync();

        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.ContainsKey("AliyundriveOpen")).IsTrue();

        var info = r.Data["AliyundriveOpen"];
        await Assert.That(info.Config.Name).IsEqualTo("AliyundriveOpen");
        await Assert.That(info.Config.DefaultRoot).IsEqualTo("root");
        await Assert.That(info.Config.Alert).IsNotNull();

        var names = info.Additional.Select(x => x.Name).ToList();
        await Assert.That(names).Contains("refresh_token");
        await Assert.That(names).Contains("root_folder_id");
        // 通用项由框架统一生成。
        var common = info.Common.Select(x => x.Name).ToList();
        await Assert.That(common).Contains("mount_path");
        await Assert.That(common).Contains("webdav_policy");
    }

    [Test]
    public async Task UninitializedDriver_RejectsOperations_InsteadOfSilentlySucceeding()
    {
        var driver = new AliyundriveOpenDriver();

        // 未初始化（无文件系统视图、无限流租约）时，任何云盘操作都必须明确失败：
        // 静默返回空结果会让上层把「配置坏了」误判成「文件不存在」。
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.GetFileLinkAsync("/file.txt", proxy: false));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.PutAsync("/file.txt", new MemoryStream([1, 2, 3])));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.PutFormAsync("/file.txt", new MemoryStream([1]), "file.txt"));
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
