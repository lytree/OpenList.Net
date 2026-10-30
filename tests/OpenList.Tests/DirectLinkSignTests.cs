using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// 直链签名策略的回归测试，对齐 Go 的 <c>needSign</c> 语义：
/// <list type="bullet">
/// <item>默认配置（<c>sign_all=false</c>、存储未开 <c>enable_sign</c>）→ 不发签名、也不校验，
/// <c>/d/</c> 不带 <c>?sign=</c> 即可下载（<c>.strm</c>、播放器书签、第三方下载器依赖这一点）。</item>
/// <item><c>sign_all=true</c> 或该存储 <c>enable_sign=true</c> → 文件带 <c>sign</c>，<c>/d/</c> 缺签名即 401。</item>
/// <item><c>link_expiration</c>（小时）决定签名的过期时间戳，过期后拒收。</item>
/// <item><c>raw_url</c> 必须是绝对地址：外部程序不会用页面 origin 解析相对路径。</item>
/// </list>
/// </summary>
[NotInParallel("WebIntegration")]
public class DirectLinkSignTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        // 这组用例模拟的是「第三方下载器 / 播放器拿直链」的匿名视角（不带 token 取 fs/get），
        // 前置条件是首页对游客开放（home_guest_access=true，公开只读站形态）；
        // 默认策略下匿名请求会先被访客开关挡成 401，测不到签名逻辑。
        await factory.SetSettingAsync("home_guest_access", "true");
        return (factory, client);
    }

    /// <summary>按前端的方式取对象：读 fs/get 的 data。</summary>
    private static async Task<JsonElement> GetObjAsync(HttpClient client, string path)
    {
        var resp = await client.PostAsync("/api/fs/get", JsonContent.Create(new { path }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").Clone();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string path)
    {
        var resp = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("content").EnumerateArray().ToList();
    }

    private static ISignService Signs(TestWebAppFactory factory)
        => factory.Services.GetRequiredService<ISignService>();

    [Test]
    public async Task Default_Config_Direct_Link_Needs_No_Sign()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "hello-default"u8.ToArray());

            var obj = await GetObjAsync(client, "/test/a.txt");
            // 默认配置下不签：前端此时不拼 ?sign=，直链就靠 /d/ 放行。
            await Assert.That(obj.GetProperty("sign").ValueKind).IsEqualTo(JsonValueKind.Null);

            var direct = await client.GetAsync("/d/test/a.txt");
            await Assert.That(direct.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await direct.Content.ReadAsStringAsync()).IsEqualTo("hello-default");

            // raw_url 同样不带签名，且必须是绝对地址（外部程序直接使用）。
            var rawUrl = obj.GetProperty("raw_url").GetString()!;
            await Assert.That(rawUrl.StartsWith("http://localhost/p/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(rawUrl.Contains("sign=", StringComparison.Ordinal)).IsFalse();

            var proxied = await client.GetAsync(rawUrl);
            await Assert.That(proxied.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task List_Has_No_Sign_By_Default()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("dir/b.txt", "b"u8.ToArray());

            var items = await ListAsync(client, "/test");
            var file = items.Single(o => o.GetProperty("name").GetString() == "a.txt");
            var dir = items.Single(o => o.GetProperty("name").GetString() == "dir");
            await Assert.That(file.GetProperty("sign").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(dir.GetProperty("sign").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
    }

    [Test]
    public async Task SignAll_Makes_Sign_Required_And_Emits_Sign()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "hello-signall"u8.ToArray());
            await factory.SetSettingAsync("sign_all", "true");

            var obj = await GetObjAsync(client, "/test/a.txt");
            var sign = obj.GetProperty("sign").GetString();
            await Assert.That(sign).IsNotNull();

            // 缺签名 → 401；带签名 → 200（前端 useLink 拼出的形式，sign 原样拼接）。
            var denied = await client.GetAsync("/d/test/a.txt");
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

            var allowed = await client.GetAsync($"/d/test/a.txt?sign={sign}");
            await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await allowed.Content.ReadAsStringAsync()).IsEqualTo("hello-signall");

            // raw_url 此时也带签名，且仍是绝对地址。
            var rawUrl = obj.GetProperty("raw_url").GetString()!;
            await Assert.That(rawUrl.StartsWith("http://localhost/p/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(rawUrl.Contains("sign=", StringComparison.Ordinal)).IsTrue();
            await Assert.That((await client.GetAsync(rawUrl)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task Storage_EnableSign_Requires_Sign_For_That_Storage()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "hello-storage"u8.ToArray());
            await factory.SetStorageEnableSignAsync("/test", true);

            var obj = await GetObjAsync(client, "/test/a.txt");
            var sign = obj.GetProperty("sign").GetString();
            await Assert.That(sign).IsNotNull();

            var denied = await client.GetAsync("/d/test/a.txt");
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

            var allowed = await client.GetAsync($"/d/test/a.txt?sign={sign}");
            await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task LinkExpiration_Sets_Expiry_And_Expired_Sign_Is_Rejected()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "hello-expire"u8.ToArray());
            await factory.SetSettingAsync("sign_all", "true");
            await factory.SetSettingAsync("link_expiration", "1");

            var obj = await GetObjAsync(client, "/test/a.txt");
            var sign = obj.GetProperty("sign").GetString()!;
            var expireAt = long.Parse(sign[(sign.LastIndexOf(':') + 1)..]);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // link_expiration=1 小时 → 过期时间戳应落在 1 小时之后（而非 0 = 永不过期）。
            await Assert.That(expireAt > now + 3500).IsTrue();
            await Assert.That(expireAt <= now + 3700).IsTrue();

            // 手工造一张已过期的签名：校验必须拒绝。
            var expired = Signs(factory).Sign("/test/a.txt", now - 10);
            var denied = await client.GetAsync($"/d/test/a.txt?sign={Uri.EscapeDataString(expired)}");
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task Verify_Tolerates_Unpadded_Signature()
    {
        var factory = new TestWebAppFactory();
        await using (factory)
        {
            _ = factory.CreateClient();
            var signs = Signs(factory);

            var padded = signs.Sign("/test/a.txt", 0);
            await Assert.That(signs.Verify("/test/a.txt", padded)).IsTrue();

            // 早期版本生成的是去掉 '=' 填充的签名（Go 是带填充的 URLEncoding），两者都要能验过。
            var unpadded = padded.Replace("=", string.Empty);
            await Assert.That(signs.Verify("/test/a.txt", unpadded)).IsTrue();

            // 签名与路径必须绑定。
            await Assert.That(signs.Verify("/test/b.txt", padded)).IsFalse();
        }
    }
}
