using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Integration tests for /sad/{sid} sharing archive route.
/// /sad/ 是历史遗留入口：分享为只读能力，匿名访客不得借它解压写盘，
/// 因此恒定返回 403，且存储内不产生任何新文件。
/// </summary>
[NotInParallel("WebIntegration")]
public class ShareDownloadEndpointsTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        return (factory, client);
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var loginResp = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { username = "admin", password = "admin" }));
        var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonDocument>();
        var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return token;
    }

    private static async Task<string> LoginAndCreateShareAsync(HttpClient client, string path)
    {
        await LoginAsync(client);
        var createResp = await client.PostAsync("/api/share/create",
            JsonContent.Create(new { files = new[] { path } }));
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonDocument>();
        return createJson!.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    [Test]
    public async Task Sad_Rejects_Invalid_Share_Code()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.GetAsync("/sad/nonexistent");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
    }

    [Test]
    public async Task Sad_Rejects_Archive_Extraction_As_ReadOnly()
    {
        // 分享只读：/sad 不再解压。旧实现在这里把归档展开到分享根所在目录，
        // 等于让任何拿到分享链接的人匿名写服务器存储。修复后必须 403 且无副作用。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var code = await LoginAndCreateShareAsync(client, "/test/a.zip");

            // 分享访问：清掉登录态，只带分享码。
            client.DefaultRequestHeaders.Authorization = null;
            var resp = await client.GetAsync($"/sad/{code}");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

            // 副作用断言：解压出来的条目绝不能出现在归档所在目录里。
            await LoginAsync(client);
            var listResp = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "/test" }));
            var listJson = await listResp.Content.ReadFromJsonAsync<JsonDocument>();
            var names = listJson!.RootElement.GetProperty("data").GetProperty("content")
                .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
            await Assert.That(names.Contains("hello.txt")).IsFalse();
            await Assert.That(names.Contains("hello")).IsFalse();
        }
    }

    [Test]
    public async Task Sae_Rejects_Inner_Path_Traversal()
    {
        // /sae 只允许归档内相对条目：`..` 段必须被拒，否则归档读取器会被指向包外路径。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var code = await LoginAndCreateShareAsync(client, "/test/a.zip");
            client.DefaultRequestHeaders.Authorization = null;

            var resp = await client.GetAsync($"/sae/{code}?inner=..%2F..%2Fetc%2Fpasswd");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task Sae_Streams_Inner_Entry_ReadOnly()
    {
        // 分享页也能预览归档内的只读条目；该端点绝不写盘。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var code = await LoginAndCreateShareAsync(client, "/test/a.zip");
            client.DefaultRequestHeaders.Authorization = null;

            var resp = await client.GetAsync($"/sae/{code}?inner=hello.txt");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo("hello");
        }
    }

    [Test]
    public async Task Sad_Rejects_Path_Traversal_In_Share()
    {
        // 越界路径必须被拒绝（状态码由路由/端点决定：规范化后塌陷、400、403、404 都算拒绝），
        // 关键是不能读到分享范围之外的内容。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var code = await LoginAndCreateShareAsync(client, "/test/a.zip");
            client.DefaultRequestHeaders.Authorization = null;

            var resp = await client.GetAsync($"/sad/{code}/%2E%2E%2F%2E%2E%2Fetc%2Fpasswd");
            await Assert.That(resp.IsSuccessStatusCode).IsFalse();
        }
    }

    [Test]
    public async Task Sad_Head_Probes_Archive_Size()
    {
        // HEAD /sad/{sid} reuses the /sd/ stream handler (no decompression side-effect).
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var content = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03, 0x04 };
            await factory.WriteFileAsync("a.zip", content);
            var code = await LoginAndCreateShareAsync(client, "/test/a.zip");
            client.DefaultRequestHeaders.Authorization = null;

            var req = new HttpRequestMessage(HttpMethod.Head, $"/sad/{code}");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }
}
