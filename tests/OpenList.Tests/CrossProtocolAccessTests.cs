using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenList.Core.Models;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// 跨协议访问控制回归：WebDAV / S3 / 分片上传都必须与 <c>/api/fs</c>
/// 使用同一套身份判定（单用户产品下「是否登录」是唯一的权限维度）。
/// </summary>
[Timeout(90_000)]
[NotInParallel("WebIntegration")]
public class CrossProtocolAccessTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        return (factory, client);
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password, CancellationToken ct = default)
    {
        var resp = await client.PostAsync("/api/auth/login", JsonContent.Create(new { username, password }), ct);
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient client, string route, object body, CancellationToken ct = default)
    {
        var resp = await client.PostAsync(route, JsonContent.Create(body), ct);
        return (await resp.Content.ReadFromJsonAsync<JsonDocument>())!;
    }

    // --- WebDAV ---

    [Test]
    public async Task WebDav_Propfind_Lists_Hidden_Entries_For_Authenticated_User(CancellationToken ct)
    {
        // 隐藏规则只约束匿名访客：已登录身份（唯一管理员）能看到全部条目。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("visible.txt", "v"u8.ToArray());
            await factory.WriteFileAsync("secret.txt", "s"u8.ToArray());
            await factory.SeedMetaAsync(new Meta { Path = "/test", Hide = "^secret", HideSub = true });

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String("admin:admin"u8.ToArray()));

            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/test");
            req.Headers.Add("Depth", "1");
            var resp = await client.SendAsync(req, ct);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.MultiStatus);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("visible.txt");
            await Assert.That(xml).Contains("secret.txt");
        }
    }

    // --- S3 ---

    [Test]
    public async Task S3_ListObjects_Returns_Keys_Relative_To_Bucket(CancellationToken ct)
    {
        // key 必须是桶内相对路径：把挂载前缀（test/）暴露出去会让客户端拼出错误对象地址。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());
            var token = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var resp = await client.GetAsync("/s3/test", ct);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("<Key>a.txt</Key>");
            await Assert.That(xml).DoesNotContain("<Key>test/a.txt</Key>");
        }
    }

    [Test]
    public async Task S3_Put_Denied_For_Anonymous(CancellationToken ct)
    {
        // 无凭据的 S3 请求一律 401（ResolveUserAsync 解析不到身份）。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.PutAsync("/s3/test/x.txt", new StringContent("x"), ct);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    // --- 分片上传归属 ---

    [Test]
    public async Task Multipart_Chunk_Rejects_Foreign_Upload_Id(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var adminToken = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);
            await factory.SeedUserAsync("uploader", "pw");

            // admin 建会话
            var init = new HttpRequestMessage(HttpMethod.Post, "/api/fs/multipart/init");
            init.Headers.Add("File-Path", Uri.EscapeDataString("/test/big.bin"));
            init.Headers.Add("X-File-Size", "10");
            init.Headers.Add("X-Chunk-Size", "1048576");
            init.Content = new StringContent("");
            var initResp = await client.SendAsync(init, ct);
            var initJson = await initResp.Content.ReadFromJsonAsync<JsonDocument>();
            var uploadId = initJson!.RootElement.GetProperty("data").GetProperty("upload_id").GetString()!;

            // 另一个账号拿同一个 uploadId 传分片 → 按「会话不存在」拒绝
            var userToken = await LoginAsync(client, "uploader", "pw");
            client.DefaultRequestHeaders.Authorization = new("Bearer", userToken);
            var chunk = new HttpRequestMessage(HttpMethod.Put, "/api/fs/multipart/chunk");
            chunk.Headers.Add("X-Upload-Id", uploadId);
            chunk.Headers.Add("X-Chunk-Index", "0");
            chunk.Content = new ByteArrayContent("0123456789"u8.ToArray());
            var chunkResp = await client.SendAsync(chunk, ct);
            var chunkJson = await chunkResp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(chunkJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(404);
        }
    }

    [Test]
    public async Task Multipart_Init_Rejected_When_Upload_Disabled(CancellationToken ct)
    {
        // multipart_enabled 关闭时 /init 返回 403（镜像 Go MultipartInit）。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.SetSettingAsync("multipart_enabled", "false");

            var adminToken = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);
            var init = new HttpRequestMessage(HttpMethod.Post, "/api/fs/multipart/init");
            init.Headers.Add("File-Path", Uri.EscapeDataString("/test/big.bin"));
            init.Headers.Add("X-File-Size", "10");
            init.Content = new StringContent("");
            var resp = await client.SendAsync(init, ct);
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
        }
    }
}
