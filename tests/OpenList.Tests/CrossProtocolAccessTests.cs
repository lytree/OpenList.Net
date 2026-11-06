using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpenList.Core.Models;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// 跨协议访问控制回归：WebDAV / S3 / 归档解压 / 分片上传都必须与 <c>/api/fs</c>
/// 使用同一套「权限位 + meta + BasePath」判定。
/// <para>
/// 这些用例针对的是一类系统性缺陷：授权此前只写在 <c>FsEndpoints</c> 里，
/// 其它协议直接调用文件服务，于是「/api/fs 挡住了，别的入口没挡」。
/// </para>
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

    private static async Task CreateUserAsync(HttpClient client, string username, string password, int permission, CancellationToken ct = default)
    {
        var resp = await client.PostAsync("/api/admin/user/create",
            JsonContent.Create(new { username, password, role = 0, permission }), ct);
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient client, string route, object body, CancellationToken ct = default)
    {
        var resp = await client.PostAsync(route, JsonContent.Create(body), ct);
        return (await resp.Content.ReadFromJsonAsync<JsonDocument>())!;
    }

    // --- WebDAV ---

    [Test]
    public async Task WebDav_ReadOnly_User_Can_Browse_But_Cannot_Write(CancellationToken ct)
    {
        // 只读账号（permission=0）不需要写权限位即可浏览；写必须 403。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());
            var token = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            await CreateUserAsync(client, "reader", "pw", permission: 0);

            // 切到只读账号（Basic）
            client.DefaultRequestHeaders.Authorization =
                new("Basic", Convert.ToBase64String("reader:pw"u8.ToArray()));

            var get = await client.GetAsync("/dav/test/a.txt", ct);
            await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var put = await client.PutAsync("/dav/test/new.txt", new StringContent("x"), ct);
            await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
    }

    [Test]
    public async Task WebDav_Propfind_Filters_Hidden_Entries(CancellationToken ct)
    {
        // 隐藏规则必须同样作用于 WebDAV：否则「/api/fs 看不到」的条目在资源管理器里照样列得出来。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("visible.txt", "v"u8.ToArray());
            await factory.WriteFileAsync("secret.txt", "s"u8.ToArray());
            await factory.SeedMetaAsync(new Meta { Path = "/test", Hide = "^secret", HideSub = true });

            var token = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            await CreateUserAsync(client, "nosy", "pw", permission: 0);
            client.DefaultRequestHeaders.Authorization =
                new("Basic", Convert.ToBase64String("nosy:pw"u8.ToArray()));

            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/test");
            req.Headers.Add("Depth", "1");
            var resp = await client.SendAsync(req, ct);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.MultiStatus);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("visible.txt");
            await Assert.That(xml).DoesNotContain("secret.txt");
        }
    }

    [Test]
    public async Task WebDav_Read_Denied_When_Meta_ReadUsers_Excludes_User(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());
            // ReadUsers 只放一个不存在的 id：所有非匹配用户都应被拒。
            await factory.SeedMetaAsync(new Meta { Path = "/test", ReadUsers = [987654321], ReadUsersSub = true });

            var token = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            await CreateUserAsync(client, "outsider", "pw", permission: 0);
            client.DefaultRequestHeaders.Authorization =
                new("Basic", Convert.ToBase64String("outsider:pw"u8.ToArray()));

            var get = await client.GetAsync("/dav/test/a.txt", ct);
            await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
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
    public async Task S3_Put_Denied_For_ReadOnly_User(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var token = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            await CreateUserAsync(client, "s3reader", "pw", permission: 0);
            var userToken = await LoginAsync(client, "s3reader", "pw");
            client.DefaultRequestHeaders.Authorization = new("Bearer", userToken);

            var resp = await client.PutAsync("/s3/test/x.txt", new StringContent("x"), ct);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
    }

    // --- 归档解压 ---

    [Test]
    public async Task Archive_Decompress_Requires_Write_Permission_And_Writes_Nothing(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var token = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            await CreateUserAsync(client, "noperm", "pw", permission: 0);

            var userToken = await LoginAsync(client, "noperm", "pw");
            client.DefaultRequestHeaders.Authorization = new("Bearer", userToken);
            var resp = await PostJsonAsync(client, "/api/fs/archive/decompress", new
            {
                src_dir = "/test",
                dst_dir = "/test",
                name = new[] { "a.zip" },
                put_into_new_dir = false,
            });
            await Assert.That(resp.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);

            // 副作用断言：管理员的视角里仍然只有 a.zip。
            var adminToken = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);
            var list = await PostJsonAsync(client, "/api/fs/list", new { path = "/test" }, ct);
            var names = list.RootElement.GetProperty("data").GetProperty("content")
                .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
            await Assert.That(names.Contains("hello.txt")).IsFalse();
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
            await CreateUserAsync(client, "uploader", "pw", permission: 1 << 3);

            // admin 建会话
            var init = new HttpRequestMessage(HttpMethod.Post, "/api/fs/multipart/init");
            init.Headers.Add("File-Path", Uri.EscapeDataString("/test/big.bin"));
            init.Headers.Add("X-File-Size", "10");
            init.Headers.Add("X-Chunk-Size", "1048576");
            init.Content = new StringContent("");
            var initResp = await client.SendAsync(init, ct);
            var initJson = await initResp.Content.ReadFromJsonAsync<JsonDocument>();
            var uploadId = initJson!.RootElement.GetProperty("data").GetProperty("upload_id").GetString()!;

            // 另一个用户拿同一个 uploadId 传分片 → 按「会话不存在」拒绝
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
    public async Task Multipart_Init_Rejects_Upload_Disabled_And_Missing_Write_Permission(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var adminToken = await LoginAsync(client, "admin", "admin");
            client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);
            await CreateUserAsync(client, "readonly", "pw", permission: 0);

            var userToken = await LoginAsync(client, "readonly", "pw");
            client.DefaultRequestHeaders.Authorization = new("Bearer", userToken);
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
