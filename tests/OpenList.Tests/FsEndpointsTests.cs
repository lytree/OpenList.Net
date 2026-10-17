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

    [Test]
    public async Task List_Allows_Anonymous_Guest_Access()
    {
        // Go allows guest access to /api/fs/list via Auth(true) middleware.
        // .NET mirrors this with AllowAnonymous on the read endpoints.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
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
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/dirs",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
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
    public async Task List_Respects_User_BasePath_Isolation()
    {
        // 镜像 Go：user.JoinPath 把请求路径钉在用户 BasePath 之内，越界相对路径返回 403。
        // 这里验证非 admin 用户的根请求落在自己的 BasePath 内，且看不到兄弟目录。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("writable/a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("other/b.txt", "b"u8.ToArray());

            // 以 admin 建一个 BasePath=/test/writable 的普通用户（权限位全开，避免权限位干扰路径断言）。
            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "admin", "admin"));
            var create = await client.PostAsync("/api/admin/user/create", JsonContent.Create(new
            {
                username = "bob",
                password = "pw",
                role = 0,
                base_path = "/test/writable",
                permission = 0x71FF,
            }));
            var createJson = await create.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(createJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);

            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "bob", "pw"));

            // 根路径请求被套到 bob 的 BasePath 内：只看得到自己的文件，看不到兄弟目录内容。
            var resp = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "/" }));
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            var names = json.RootElement.GetProperty("data").GetProperty("content")
                .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
            await Assert.That(names.Any(n => n == "a.txt")).IsTrue();
            await Assert.That(names.Any(n => n == "other")).IsFalse();

            // 越界相对路径被拒绝（镜像 Go utils.JoinBasePath → errs.RelativePath）。
            var esc = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "../other" }));
            var escJson = await esc.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(escJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsync("/api/auth/login", JsonContent.Create(new { username, password }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }
}
