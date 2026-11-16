using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Integration tests for /api/fs endpoints, focusing on guest (anonymous) access
/// to the read endpoints (list/get/dirs/search) — Go's fsAndShare group behaviour.
/// Uses TestWebAppFactory with a Local storage mounted at /test.
/// </summary>
[NotInParallel("WebIntegration")]
public class FsEndpointsTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        return (factory, client);
    }

    /// <summary>
    /// 打开「首页允许游客浏览」开关（<c>home_guest_access</c>）。
    /// <para>
    /// 默认是 **false** —— 首页（文件浏览）需要登录，匿名 <c>/api/fs/*</c> 直接 401。
    /// 下面几个「游客能读」的用例都建立在这条开关打开的前提下（公开只读分享站形态）。
    /// </para>
    /// </summary>
    private static Task AllowGuestBrowsingAsync(TestWebAppFactory factory)
        => factory.SetSettingAsync("home_guest_access", "true");

    [Test]
    public async Task List_Allows_Anonymous_Guest_Access()
    {
        // Go allows guest access to /api/fs/list via Auth(true) middleware.
        // .NET mirrors this with AllowAnonymous on the read endpoints.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await AllowGuestBrowsingAsync(factory);

            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            // No Authorization header — anonymous guest request.
            var resp = await client.PostAsync("/api/fs/list",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("total").GetInt32()).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task Get_Allows_Anonymous_Guest_Access()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await AllowGuestBrowsingAsync(factory);

            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/get",
                JsonContent.Create(new { path = "/test/a.txt" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Dirs_Allows_Anonymous_Guest_Access()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await AllowGuestBrowsingAsync(factory);

            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/dirs",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task List_Denies_Anonymous_When_Home_Guest_Access_Is_Off()
    {
        // 默认策略：首页（文件浏览）需要登录。关掉 home_guest_access 时匿名 /fs/list 直接 401，
        // 而不是「200 + 空列表」—— 前端据此回登录页，第三方客户端也不会误以为拿到了目录。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/list",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(401);
        }
    }

    [Test]
    public async Task Mkdir_Still_Requires_Auth()
    {
        // Write endpoints (mkdir, rename, move, copy, remove, put, ...) must still require auth.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.PostAsync("/api/fs/mkdir",
                JsonContent.Create(new { path = "/test/newdir" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task Remove_Rejects_RelativePathNames()
    {
        // 镜像 Go checkRelativePath：/fs/remove 的 names 只允许纯名称；
        // "../../secret.txt" 会被 UPath 归一化解析后越出 dir 删除兄弟文件，必须 403。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("secret.txt", "s"u8.ToArray());
            await factory.WriteFileAsync("writable/a.txt", "a"u8.ToArray());

            var loginResp = await client.PostAsync("/api/auth/login",
                JsonContent.Create(new { username = "admin", password = "admin" }));
            var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonDocument>();
            var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            foreach (var name in new[] { "../../secret.txt", @"..\..\secret.txt" })
            {
                var resp = await client.PostAsync("/api/fs/remove",
                    JsonContent.Create(new { dir = "/test/writable", names = new[] { name } }));
                var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
                await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
            }

            // 兄弟文件仍在。
            var getResp = await client.PostAsync("/api/fs/get",
                JsonContent.Create(new { path = "/test/secret.txt" }));
            var getJson = await getResp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(getJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task List_With_Valid_Token_Resolves_Real_User()
    {
        // When a valid JWT is presented, the user is resolved (not guest).
        // This verifies that AllowAnonymous doesn't break JWT parsing.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            // Login as admin first.
            var loginResp = await client.PostAsync("/api/auth/login",
                JsonContent.Create(new { username = "admin", password = "admin" }));
            var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonDocument>();
            var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var resp = await client.PostAsync("/api/fs/list",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task DirectUpload_Info_Rejects_NonName_FileName()
    {
        // 镜像 Go FsGetDirectUploadInfo 的 checkRelativePath：file_name 必须是纯名称。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("base/.keep", "k"u8.ToArray());

            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "admin", "admin"));

            // file_name 含路径成分（/ \、. ..、空）→ 403。
            foreach (var bad in new[] { "../evil.txt", "sub/evil.txt", @"..\evil.txt", "", "..", "." })
            {
                var invalid = await PostJsonAsync(client, "/api/fs/get_direct_upload_info",
                    new { path = "/test/base", file_name = bad, file_size = 10 });
                await Assert.That(invalid.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
            }

            // 合法 file_name → 通过门禁（当前无驱动实现直传，服务层回 501，非 403）。
            var ok = await PostJsonAsync(client, "/api/fs/get_direct_upload_info",
                new { path = "/test/base", file_name = "a.txt", file_size = 10 });
            await Assert.That(ok.RootElement.GetProperty("code").GetInt32()).IsNotEqualTo(403);
        }
    }

    /// <summary>
    /// 等待某类型的后台任务清空。
    /// </summary>
    /// <remarks>
    /// move/copy 是任务化端点：HTTP 200 只代表任务已入队，文件操作在线程池里继续跑。
    /// 测试若在返回后立刻重建/删除同一批文件，就会和还在进行中的任务撞上 ——
    /// Windows 上表现为 <c>The process cannot access the file … because it is being used by another process</c>。
    /// </remarks>
    private static async Task WaitTasksAsync(HttpClient client, string type)
    {
        for (var i = 0; i < 150; i++)
        {
            var resp = await client.GetAsync($"/api/task/{type}/undone");
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            var data = json!.RootElement.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 0) return;
            await Task.Delay(20);
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsync("/api/auth/login", JsonContent.Create(new { username, password }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient client, string route, object body)
    {
        var resp = await client.PostAsync(route, JsonContent.Create(body));
        return (await resp.Content.ReadFromJsonAsync<JsonDocument>())!;
    }
}
