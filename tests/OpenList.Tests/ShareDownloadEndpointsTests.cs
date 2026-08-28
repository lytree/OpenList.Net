using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Integration tests for /sad/{sid} sharing archive extract route (P1-6).
/// Verifies share verification and that the handler returns 501 when the underlying
/// driver does not implement IFileSystemArchiveDecompress.
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

    private static async Task<string> LoginAndCreateShareAsync(HttpClient client, string path)
    {
        var loginResp = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { username = "admin", password = "admin" }));
        var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonDocument>();
        var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

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
    public async Task Sad_Decompresses_Archive_From_Share()
    {
        // LocalDriver now implements IFileSystemArchiveDecompress (SharpCompress), so /sad/
        // decompresses the shared archive instead of returning 501.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            var code = await LoginAndCreateShareAsync(client, "/test/a.zip");
            client.DefaultRequestHeaders.Authorization = null;

            var resp = await client.GetAsync($"/sad/{code}");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
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
