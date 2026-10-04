using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenList.Core.Models;
using OpenList.Core.Torrent;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// /api/fs/torrent/parse 端到端集成测试：登录管理员 → 调用 parse → 拿到种子详情。
/// 与其他 Web 集成测试同串行组，避免 Serilog 静态 logger 与 SQLite 共享实例冲突。
/// </summary>
[NotInParallel("WebIntegration")]
public class FsTorrentEndpointTests
{
    [Test]
    public async Task Parse_Endpoint_ReturnsTorrentDetails()
    {
        await using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var bytes = BuildSingleFileTorrentBytes("hello.txt", 11);
        var b64 = Convert.ToBase64String(bytes);

        var resp = await client.PostAsJsonAsync("/api/fs/torrent/parse", new { torrent_data = b64 });
        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<Resp<Core.Interfaces.TorrentParsedResp>>();
        await Assert.That(body).IsNotNull();
        await Assert.That(body!.Code).IsEqualTo(200);
        await Assert.That(body.Data!.Name).IsEqualTo("hello.txt");
        await Assert.That(body.Data.TotalSize).IsEqualTo(11);
    }

    [Test]
    public async Task Parse_Endpoint_Rejects_EmptyTorrentData()
    {
        await using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync("/api/fs/torrent/parse", new { torrent_data = "" });
        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<Resp<Core.Interfaces.TorrentParsedResp>>();
        await Assert.That(body!.Code).IsEqualTo(400);
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        await Assert.That(resp.IsSuccessStatusCode).IsTrue();
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private static byte[] BuildSingleFileTorrentBytes(string name, long length)
    {
        var pieces = new byte[20];
        for (int i = 0; i < pieces.Length; i++) pieces[i] = 0xAB;
        var info = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["name"] = new Bencode.BytesNode(System.Text.Encoding.UTF8.GetBytes(name)),
            ["piece length"] = new Bencode.IntNode(16384),
            ["pieces"] = new Bencode.BytesNode(pieces),
            ["length"] = new Bencode.IntNode(length),
        });
        var infoBytes = Bencode.Encode(info);
        var root = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["info"] = new Bencode.BytesNode(infoBytes),
        });
        return Bencode.Encode(root);
    }
}