using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// <c>non_efs_zip_encoding</c> 的端到端接线测试：设置写进库 → 归档列表按它解码条目名。
/// <para>
/// 驱动层的行为由 <see cref="LegacyZipEncodingTests"/> 覆盖；这里守的是**接线**——
/// <c>FileSystemService</c> 是否真的把设置读出来并传给驱动（读漏了，驱动实现再对也没用）。
/// </para>
/// </summary>
[NotInParallel("WebIntegration")]
public class ArchiveEncodingSettingTests
{
    private const string ChineseEntry = "中文目录/说明.txt";

    /// <summary>登录管理员并返回带 Bearer 的客户端。</summary>
    private static async Task<HttpClient> AdminClientAsync(TestWebAppFactory factory)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        var json = await login.Content.ReadFromJsonAsync<JsonDocument>();
        var token = json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    /// <summary>取归档列表里的条目名。</summary>
    private static async Task<List<string>> ListNamesAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/fs/archive/list", new { path = "/test/legacy.zip" });
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("content")
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString() ?? "")
            .ToList();
    }

    /// <summary>准备一个挂载了 GBK 名条目的隔离实例。</summary>
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync(string? encoding)
    {
        var factory = new TestWebAppFactory();
        var client = await AdminClientAsync(factory);
        await factory.SeedStorageAsync();
        await factory.WriteFileAsync("legacy.zip", LegacyZipBuilder.BuildWithGbkName(ChineseEntry, "payload"));
        if (encoding is not null) await factory.SetSettingAsync("non_efs_zip_encoding", encoding);
        return (factory, client);
    }

    [Test]
    public async Task Archive_List_Uses_Configured_Encoding()
    {
        var (factory, client) = await SetupAsync("gbk");
        await using (factory)
        {
            await Assert.That(await ListNamesAsync(client)).Contains(ChineseEntry);
        }
    }

    [Test]
    public async Task Archive_List_Without_Setting_Keeps_Default_Decoding()
    {
        var (factory, client) = await SetupAsync(encoding: null);
        await using (factory)
        {
            // 未配置时按 UTF-8 解，中文名解不出来 —— 与驱动层测试互为对照。
            await Assert.That(await ListNamesAsync(client)).DoesNotContain(ChineseEntry);
        }
    }
}
