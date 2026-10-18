using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenList.Core.Interfaces;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Integration tests for the signed archive routes /ad/, /ap/, /ae/ (P1-5).
/// Verifies signature enforcement and that handlers return 501 when the underlying
/// driver does not implement IFileSystemArchiveReader / IFileSystemArchiveDecompress.
/// <para>
/// 签名是「按策略生效」的：默认配置下不发也不校验（对齐 Go 的 needSign）。
/// 本类验证的是<b>启用签名后</b>的行为，故 Setup 里显式打开 <c>sign_all</c>。
/// </para>
/// </summary>
[NotInParallel("WebIntegration")]
public class SignEndpointsTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        // 这些用例断言「缺签名 → 401 / 带签名 → 200」，必须先开启签名策略，
        // 否则默认配置下 needSign 为 false，缺签名也会被放行。
        await factory.SetSettingAsync("sign_all", "true");
        return (factory, client);
    }

    private static async Task<string> SignAsync(HttpClient client, string path)
    {
        // Login as admin, then use the /api/fs/link endpoint (or internal SignService) to
        // obtain a signature. The simplest path is to hit /api/fs/get which returns RawUrl
        // containing the sign parameter.
        var loginResp = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { username = "admin", password = "admin" }));
        var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonDocument>();
        var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var resp = await client.PostAsync("/api/fs/get",
            JsonContent.Create(new { path }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        var rawUrl = json!.RootElement.GetProperty("data").GetProperty("raw_url").GetString()!;
        // raw_url 形如 "{base}/p/<path>?sign=<sig>"（绝对地址）；取出 sign 的值。
        var idx = rawUrl.IndexOf("sign=", StringComparison.Ordinal);
        return rawUrl[(idx + 5)..];
    }

    [Test]
    public async Task Ad_Rejects_Missing_Signature()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var resp = await client.GetAsync("/ad/test/a.zip");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task Ad_Lists_Archive_Entries_With_Valid_Signature()
    {
        // LocalDriver now implements IFileSystemArchiveReader (SharpCompress), so /ad/
        // returns the archive's entry list instead of 501.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var sign = await SignAsync(client, "/test/a.zip");
            // Clear auth header — /ad/ is anonymous (signature-only).
            client.DefaultRequestHeaders.Authorization = null;

            var resp = await client.GetAsync($"/ad/test/a.zip?sign={sign}");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            var content = json.RootElement.GetProperty("data").GetProperty("content");
            await Assert.That(content.GetArrayLength()).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Ae_Decompresses_Archive_With_Valid_Signature()
    {
        // LocalDriver now implements IFileSystemArchiveDecompress (SharpCompress), so /ae/
        // decompresses the archive into its parent directory instead of returning 501.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var sign = await SignAsync(client, "/test/a.zip");
            client.DefaultRequestHeaders.Authorization = null;

            var resp = await client.GetAsync($"/ae/test/a.zip?sign={sign}");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Ap_Streams_Archive_File_With_Valid_Signature()
    {
        // /ap/ streams the archive file itself (same as /d/), so it should succeed
        // regardless of whether the driver implements archive interfaces.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var content = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x01, 0x02 };
            await factory.WriteFileAsync("a.zip", content);
            var sign = await SignAsync(client, "/test/a.zip");
            client.DefaultRequestHeaders.Authorization = null;

            var resp = await client.GetAsync($"/ap/test/a.zip?sign={sign}");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            await Assert.That(bytes.Length).IsEqualTo(content.Length);
        }
    }
}
