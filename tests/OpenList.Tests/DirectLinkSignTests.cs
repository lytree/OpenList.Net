using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// 直链签名的回归测试：fs 接口必须给文件带上 <c>sign</c>，前端才能拼出可用的 <c>/d/</c> 直链。
/// <para>
/// 背景：前端 <c>hooks/useLink.ts</c> 生成 <c>/d/{path}</c> 时，只有 <c>obj.sign</c> 非空才追加
/// <c>&amp;sign=</c>；而 <c>/d/</c> 端点对空签名一律 401。服务端曾经从不填 <c>sign</c>，
/// 于是所有交给外部程序（PotPlayer / VLC / 下载器）的直链都是 401 —— 表现就是「点击无法打开」。
/// 这些程序不会携带登录凭据，所以修复只能在「把签名交给前端」这一侧。
/// </para>
/// </summary>
[NotInParallel("WebIntegration")]
public class DirectLinkSignTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        return (factory, client);
    }

    /// <summary>按前端的方式取 sign：读 fs/get 的 data.sign，再原样拼到 /d/ 上（前端不做 URL 编码）。</summary>
    private static async Task<JsonElement> GetObjAsync(HttpClient client, string path)
    {
        var resp = await client.PostAsync("/api/fs/get", JsonContent.Create(new { path }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").Clone();
    }

    [Test]
    public async Task Get_Returns_Sign_For_File_And_Direct_Link_Downloads()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "hello-direct"u8.ToArray());

            var obj = await GetObjAsync(client, "/test/a.txt");
            var sign = obj.GetProperty("sign").GetString();
            await Assert.That(sign).IsNotNull();

            // 前端 useLink 拼出的形式：/d/{path}?sign={sign}，sign 原样拼接（含尾部的 ":0"）。
            var download = await client.GetAsync($"/d/test/a.txt?sign={sign}");
            await Assert.That(download.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await download.Content.ReadAsStringAsync()).IsEqualTo("hello-direct");
        }
    }

    [Test]
    public async Task List_Items_Carry_Sign_For_Files_But_Not_Directories()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("dir/b.txt", "b"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "/test" }));
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            var items = json!.RootElement.GetProperty("data").GetProperty("content").EnumerateArray().ToList();

            var file = items.Single(o => o.GetProperty("name").GetString() == "a.txt");
            var dir = items.Single(o => o.GetProperty("name").GetString() == "dir");
            await Assert.That(file.GetProperty("sign").GetString()).IsNotNull();
            await Assert.That(dir.GetProperty("sign").ValueKind).IsEqualTo(JsonValueKind.Null);

            // 目录下的列表项同样可用：/d/test/dir/b.txt?sign=...
            var fileB = (await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "/test/dir" })))
                .Content.ReadFromJsonAsync<JsonDocument>().Result!
                .RootElement.GetProperty("data").GetProperty("content")[0];
            var dl = await client.GetAsync($"/d/test/dir/b.txt?sign={fileB.GetProperty("sign").GetString()}");
            await Assert.That(dl.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task Raw_Url_Is_Signed_For_Path_Without_Leading_Slash()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "x"u8.ToArray());

            // 调用方省略前导 "/" 时，raw_url 也必须签对（否则 /p/ 校验必然失败）。
            var obj = await GetObjAsync(client, "test/a.txt");
            var rawUrl = obj.GetProperty("raw_url").GetString();
            await Assert.That(rawUrl).IsNotNull();
            var resp = await client.GetAsync(rawUrl);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }
}
