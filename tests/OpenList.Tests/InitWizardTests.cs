using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// 初始化向导端点测试（对应 Go <c>server/handles/setup.go</c>）：
/// <c>GET/POST /api/public/init_status</c> 与 <c>POST /api/public/init/setup</c>。
/// <para>
/// TestWebAppFactory 会 seed 默认管理员，因此这些用例覆盖"已初始化"分支；
/// "未初始化 → 创建管理员" 的服务层分支由
/// <c>UserServiceTests.GetAdminAsync_*</c> 覆盖。
/// </para>
/// </summary>
[NotInParallel("WebIntegration")]
public class InitWizardTests
{
    [Test]
    public async Task InitStatus_Reports_Initialized_True_When_Admin_Exists()
    {
        var factory = new TestWebAppFactory();
        await using (factory)
        {
            using var client = factory.CreateClient();
            await factory.SeedStorageAsync();

            var resp = await client.GetAsync("/api/public/init_status");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("data").GetProperty("initialized").GetBoolean()).IsTrue();
        }
    }

    [Test]
    public async Task InitSetup_Rejected_When_Already_Initialized()
    {
        var factory = new TestWebAppFactory();
        await using (factory)
        {
            using var client = factory.CreateClient();
            await factory.SeedStorageAsync();

            var resp = await client.PostAsync("/api/public/init/setup",
                JsonContent.Create(new { username = "newadmin", password = "abcd", site_title = "Wizard" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(400);
            await Assert.That(json.RootElement.GetProperty("message").GetString()).Contains("already been initialized");
        }
    }

    [Test]
    public async Task InitSetup_Requires_Username_And_Password()
    {
        var factory = new TestWebAppFactory();
        await using (factory)
        {
            using var client = factory.CreateClient();
            await factory.SeedStorageAsync();

            var resp = await client.PostAsync("/api/public/init/setup",
                JsonContent.Create(new { username = "", password = "" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(400);
        }
    }
}
