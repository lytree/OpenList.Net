using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// 「访问控制」设置（group 13）在端点层的落地：媒体与首页两个门面**默认值不同**。
/// <list type="bullet">
/// <item>媒体（<c>/api/media</c>）默认对游客开放 —— 它是站点落地页，也是公开门面。</item>
/// <item>首页（<c>/api/fs</c>）默认要求登录 —— 文件树不该裸奔；放开后游客只读。</item>
/// </list>
/// <para>
/// 这里只钉「开关关掉之后匿名一定进不来、登录用户一定进得来」这两条线：
/// 具体每个端点的业务行为由各自的测试负责。
/// </para>
/// </summary>
[NotInParallel("WebIntegration")]
public class GuestAccessEndpointTests
{
    /// <summary>内置管理员登录，返回带 Bearer 的客户端。</summary>
    private static async Task<HttpClient> LoginAdminAsync(TestWebAppFactory factory)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { username = "admin", password = "admin" }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        var token = json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Test]
    public async Task Media_Is_Public_By_Default()
    {
        await using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/media/items");
        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
    }

    [Test]
    public async Task Media_Rejects_Anonymous_When_Guest_Access_Is_Off()
    {
        await using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SetSettingAsync("media_guest_access", "false");
        var anon = await client.GetAsync("/api/media/items");
        await Assert.That(anon.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        var anonJson = await anon.Content.ReadFromJsonAsync<JsonDocument>();
        await Assert.That(anonJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(401);

        // 关的是「访客」，不是「所有人」：登录用户照旧能进。
        var admin = await LoginAdminAsync(factory);
        var authed = await admin.GetAsync("/api/media/items");
        await Assert.That(authed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Home_Rejects_Anonymous_By_Default()
    {
        await using var factory = new TestWebAppFactory();
        await factory.SeedStorageAsync();

        var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "/test" }));
        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Home_Allows_Anonymous_When_Guest_Access_Is_On()
    {
        await using var factory = new TestWebAppFactory();
        await factory.SeedStorageAsync();
        var client = factory.CreateClient();
        await factory.SetSettingAsync("home_guest_access", "true");
        var resp = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "/test" }));
        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
    }

    [Test]
    public async Task Both_Switches_Are_Public_Settings()
    {
        // 前端路由守卫在**未登录**时就要知道两个门面能不能进，所以这两项必须是公开设置，
        // 否则游客拿不到值，守卫只能按默认值猜 —— 会和后端口径打架。
        await using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/public/settings");
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        var data = json!.RootElement.GetProperty("data");

        await Assert.That(data.TryGetProperty("media_guest_access", out var media)).IsTrue();
        await Assert.That(media.GetString()).IsEqualTo("true");
        await Assert.That(data.TryGetProperty("home_guest_access", out var home)).IsTrue();
        await Assert.That(home.GetString()).IsEqualTo("false");
    }
}
