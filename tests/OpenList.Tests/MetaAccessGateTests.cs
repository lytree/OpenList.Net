using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenList.Core.Models;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// meta 访问门禁（密码目录 / 隐藏 / 读用户白名单）在只读链路上的回归测试。
/// <para>
/// 此前 <c>fs/list</c>、<c>fs/get</c>、<c>/d/</c> 完全没有 meta 密码校验（<c>CanAccess</c> 只存在于
/// 搜索服务），密码目录的内容照样能列出、直链照样能下。对齐 Go 后：
/// </para>
/// <list type="bullet">
/// <item><c>fs/list</c> / <c>fs/get</c> / <c>fs/dirs</c> / <c>fs/other</c> 走 <c>common.CanAccess</c>，
/// 未通过返回业务码 403（前端据此弹密码框）。</item>
/// <item>密码目录同时被 <c>needSign</c> 第三档覆盖：<c>/d/</c> 要求签名，而签名只能从通过密码校验的
/// <c>fs/get</c> 取得，从而在「/d/ 本身不校验密码」的前提下仍挡住匿名直链。</item>
/// </list>
/// </summary>
[NotInParallel("WebIntegration")]
public class MetaAccessGateTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        return (factory, client);
    }

    /// <summary>发一个 JSON 请求并取回 <c>{ code, data }</c> 根元素。</summary>
    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object body)
    {
        var resp = await client.PostAsync(url, JsonContent.Create(body));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.Clone();
    }

    private static async Task<int> ListCodeAsync(HttpClient client, string path, string? password = null)
        => (await PostAsync(client, "/api/fs/list", new { path, password })).GetProperty("code").GetInt32();

    private static async Task<int> GetCodeAsync(HttpClient client, string path, string? password = null)
        => (await PostAsync(client, "/api/fs/get", new { path, password })).GetProperty("code").GetInt32();

    /// <summary>以 admin 身份登录（内置管理员带「免密访问」权限位）。</summary>
    private static async Task LoginAdminAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { username = "admin", password = "admin" }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        var token = json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    [Test]
    public async Task Password_Dir_Requires_Password_For_List_And_Get()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("locked/a.txt", "secret"u8.ToArray());
            await factory.SeedMetaAsync(new Meta { Path = "/test/locked", Password = "pw", PSub = true });

            // 匿名：列目录与取详情都要 403（前端据此弹密码框）。
            await Assert.That(await ListCodeAsync(client, "/test/locked")).IsEqualTo(403);
            await Assert.That(await GetCodeAsync(client, "/test/locked/a.txt")).IsEqualTo(403);

            // 密码错误同样 403。
            await Assert.That(await ListCodeAsync(client, "/test/locked", "bad")).IsEqualTo(403);

            // 正确密码：放行。
            var list = await PostAsync(client, "/api/fs/list", new { path = "/test/locked", password = "pw" });
            await Assert.That(list.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(list.GetProperty("data").GetProperty("content").EnumerateArray()
                .Any(o => o.GetProperty("name").GetString() == "a.txt")).IsTrue();

            var get = await PostAsync(client, "/api/fs/get", new { path = "/test/locked/a.txt", password = "pw" });
            await Assert.That(get.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(get.GetProperty("data").GetProperty("name").GetString()).IsEqualTo("a.txt");

            // 未设密码的路径不受影响。
            await factory.WriteFileAsync("open.txt", "open"u8.ToArray());
            await Assert.That(await GetCodeAsync(client, "/test/open.txt")).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Password_Dir_Protects_Direct_Link_With_Sign()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("locked/a.txt", "secret-link"u8.ToArray());
            await factory.SeedMetaAsync(new Meta { Path = "/test/locked", Password = "pw", PSub = true });

            // /d/ 自身不校验密码（与 Go 一致），但密码目录意味着 needSign 为真 → 无签名 401。
            var denied = await client.GetAsync("/d/test/locked/a.txt");
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

            // 签名只能从通过密码校验的 fs/get 拿到。
            var get = await PostAsync(client, "/api/fs/get", new { path = "/test/locked/a.txt", password = "pw" });
            var sign = get.GetProperty("data").GetProperty("sign").GetString()!;
            await Assert.That(sign).IsNotNull();

            var allowed = await client.GetAsync($"/d/test/locked/a.txt?sign={sign}");
            await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await allowed.Content.ReadAsStringAsync()).IsEqualTo("secret-link");

            // 目录项在列表里也带上签名（前端缓存目录后点开播放不再回头请求 fs/get）。
            var list = await PostAsync(client, "/api/fs/list", new { path = "/test/locked", password = "pw" });
            var file = list.GetProperty("data").GetProperty("content").EnumerateArray()
                .Single(o => o.GetProperty("name").GetString() == "a.txt");
            await Assert.That(file.GetProperty("sign").GetString()).IsNotNull();
        }
    }

    [Test]
    public async Task Password_Without_PSub_Does_Not_Cover_Children()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("locked/sub/f.txt", "sub"u8.ToArray());
            await factory.WriteFileAsync("locked/other.txt", "other"u8.ToArray());
            // PSub=false：密码只保护 /test/locked/sub 这个目录自身，不波及子项。
            await factory.SeedMetaAsync(new Meta { Path = "/test/locked/sub", Password = "pw", PSub = false });

            await Assert.That(await ListCodeAsync(client, "/test/locked/sub")).IsEqualTo(403);
            await Assert.That(await ListCodeAsync(client, "/test/locked/sub", "pw")).IsEqualTo(200);

            // 子文件不受该密码约束（对齐 Go：MetaCoversPath(..., PSub=false) 只匹配等值路径）。
            await Assert.That(await GetCodeAsync(client, "/test/locked/sub/f.txt")).IsEqualTo(200);
            var direct = await client.GetAsync("/d/test/locked/sub/f.txt");
            await Assert.That(direct.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await direct.Content.ReadAsStringAsync()).IsEqualTo("sub");

            // 同级的其它路径完全无关。
            await Assert.That(await GetCodeAsync(client, "/test/locked/other.txt")).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Admin_Bypasses_Password_Gate()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("locked/a.txt", "admin-ok"u8.ToArray());
            await factory.SeedMetaAsync(new Meta { Path = "/test/locked", Password = "pw", PSub = true });

            await LoginAdminAsync(client);
            // 管理员权限位含「免密访问」（0x71FF 的 bit1），无需 password 即可列出。
            await Assert.That(await ListCodeAsync(client, "/test/locked")).IsEqualTo(200);
            await Assert.That(await GetCodeAsync(client, "/test/locked/a.txt")).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Hidden_Entry_Is_Denied_For_Guest_But_Allowed_For_Admin()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("h/secret.txt", "hidden"u8.ToArray());
            await factory.WriteFileAsync("h/public.txt", "public"u8.ToArray());
            await factory.SeedMetaAsync(new Meta { Path = "/test/h", Hide = "secret\\.txt", HideSub = true });

            // 隐藏规则的粒度是「条目」：目录本身可列，命中的条目不可直接访问。
            await Assert.That(await ListCodeAsync(client, "/test/h")).IsEqualTo(200);
            await Assert.That(await GetCodeAsync(client, "/test/h/secret.txt")).IsEqualTo(403);
            await Assert.That(await GetCodeAsync(client, "/test/h/public.txt")).IsEqualTo(200);

            // 管理员带「可见隐藏」权限位（0x71FF 的 bit0）。
            await LoginAdminAsync(client);
            await Assert.That(await GetCodeAsync(client, "/test/h/secret.txt")).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Read_Users_Whitelist_Blocks_Guest_List()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("ro/a.txt", "ro"u8.ToArray());
            // 白名单只含一个不存在的用户 id：游客（Id=0）不在其中，读受限。
            await factory.SeedMetaAsync(new Meta { Path = "/test/ro", ReadUsers = [9999], ReadUsersSub = true });

            await Assert.That(await ListCodeAsync(client, "/test/ro")).IsEqualTo(403);
            await Assert.That(await GetCodeAsync(client, "/test/ro/a.txt")).IsEqualTo(403);
        }
    }
}
