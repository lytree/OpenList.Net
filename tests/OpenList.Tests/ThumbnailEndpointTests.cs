using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Infrastructure.Thumbnails;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// <c>/p?type=thumb</c> 的端点级回归：
/// <list type="bullet">
/// <item>不传 <c>type</c> 时行为与升级前<b>完全一致</b>（这是最重要的一条 —— 旧播放器/下载器不能受影响）；</item>
/// <item>总开关关闭时列表不下发 <c>thumb</c>，端点回 415 + <c>X-Thumb-Reason</c>；</item>
/// <item>开启后列表下发 <c>thumb</c>，且 <c>/p/</c> 会按扩展名分流到缩略图链路；</item>
/// <item>ffmpeg 缺失时降级为 404 <c>ffmpeg-unavailable</c>，<b>不能</b>把请求打死或返回 500；</item>
/// <item>HEAD 只回头信息、不生成。</item>
/// </list>
/// </summary>
[NotInParallel("WebIntegration")]
public class ThumbnailEndpointTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync(params (string Key, string Value)[] settings)
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        await factory.SetSettingAsync("home_guest_access", "true");
        foreach (var (key, value) in settings)
            await factory.SetSettingAsync(key, value);
        // 设置快照默认 20 秒刷新一次（生产环境够用，测试里必须显式触发）。
        await factory.Services.GetRequiredService<ThumbnailOptionsProvider>().RefreshAsync();
        return (factory, client);
    }

    private static async Task<JsonElement> ListFirstAsync(HttpClient client, string path)
    {
        var resp = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("content").EnumerateArray().First().Clone();
    }

    /// <summary>按「环境变量 → 仓库内 .e2e/bin → PATH」找 ffmpeg；找不到返回 null（用例直接返回）。</summary>
    private static string? FindFfmpeg()
    {
        var fromEnv = Environment.GetEnvironmentVariable("OPENLIST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                var candidate = Path.Combine(dir.FullName, ".e2e", "bin", name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                try
                {
                    var candidate = Path.Combine(entry, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* PATH 里有非法字符的目录 */ }
            }
        }
        return null;
    }

    [Test]
    public async Task WithoutType_StillServesOriginalBytes()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "hello-original"u8.ToArray());

            // 回归保护：任何 type 之外/之外的 query 都不能改变 /p/ 的既有语义。
            var resp = await client.GetAsync("/p/test/a.txt");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo("hello-original");
        }
    }

    [Test]
    public async Task UnknownType_FallsBackToNormalDownload()
    {
        var (factory, client) = await SetupAsync(("thumb_enable", "true"));
        await using (factory)
        {
            await factory.WriteFileAsync("b.txt", "hello-other-type"u8.ToArray());

            var resp = await client.GetAsync("/p/test/b.txt?type=whatever");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo("hello-other-type");
        }
    }

    [Test]
    public async Task InvalidWidthParam_DoesNotBreakNormalDownload()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("c.txt", "hello-bad-param"u8.ToArray());

            // 缩略图参数一律按字符串收、非法值当没传：否则 w=abc 会让普通下载被判 400。
            var resp = await client.GetAsync("/p/test/c.txt?w=abc&h=&q=x");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo("hello-bad-param");
        }
    }

    [Test]
    public async Task Disabled_ListHasNoThumb_AndEndpointReturns415()
    {
        var (factory, client) = await SetupAsync(("thumb_enable", "false"));
        await using (factory)
        {
            await factory.WriteFileAsync("photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);

            var obj = await ListFirstAsync(client, "/test");
            await Assert.That(obj.GetProperty("thumb").ValueKind).IsEqualTo(JsonValueKind.Null);

            var resp = await client.GetAsync("/p/test/photo.jpg?type=thumb");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
            await Assert.That(resp.Headers.GetValues("X-Thumb-Reason").First()).IsEqualTo("disabled");
        }
    }

    [Test]
    public async Task Enabled_ListEmitsAbsoluteThumbUrl()
    {
        var (factory, client) = await SetupAsync((("thumb_enable", "true")), ("thumb_max_size", "240"));
        await using (factory)
        {
            await factory.WriteFileAsync("photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);

            var obj = await ListFirstAsync(client, "/test");
            var thumb = obj.GetProperty("thumb").GetString();
            await Assert.That(thumb).IsNotNull();
            await Assert.That(thumb!).Contains("/p/test/photo.jpg?type=thumb");
            await Assert.That(thumb!).Contains("w=240");
        }
    }

    [Test]
    public async Task Video_IsNotListedWithThumb()
    {
        var (factory, client) = await SetupAsync(("thumb_enable", "true"));
        await using (factory)
        {
            // 一个目录里 20 个视频 = 20 次远端抽帧：列表绝不下发视频缩略图。
            await factory.WriteFileAsync("movie.mp4", [0x00, 0x00, 0x00, 0x18]);

            var obj = await ListFirstAsync(client, "/test");
            await Assert.That(obj.GetProperty("thumb").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
    }

    [Test]
    public async Task Enabled_WithFfmpeg_ServesImageAndThenHitsCache()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return; // 裸机没有 ffmpeg：这条只验证「不崩」，参数正确性由 ThumbnailFfmpegTests 覆盖

        var (factory, client) = await SetupAsync(("thumb_enable", "true"), ("thumb_ffmpeg_path", ffmpeg));
        await using (factory)
        {
            // 1×1 PNG（base64）：后端会把它缩放成 96×96 的 JPEG。
            await factory.WriteFileAsync("one.png", Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

            var first = await client.GetAsync("/p/test/one.png?type=thumb&w=96&h=96");
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(first.Content.Headers.ContentType!.MediaType).IsEqualTo("image/jpeg");
            await Assert.That(first.Headers.GetValues("X-Thumb-Cache").First()).IsEqualTo("generated");
            var bytes = await first.Content.ReadAsByteArrayAsync();
            await Assert.That(bytes.Length).IsGreaterThan(200);
            await Assert.That(bytes[0]).IsEqualTo((byte)0xFF);
            await Assert.That(bytes[1]).IsEqualTo((byte)0xD8);
            await Assert.That(first.Headers.ETag).IsNotNull();

            var second = await client.GetAsync("/p/test/one.png?type=thumb&w=96&h=96");
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(second.Headers.GetValues("X-Thumb-Cache").First()).IsEqualTo("hit");
        }
    }

    [Test]
    public async Task Enabled_WithoutFfmpeg_DegradesTo404WithReason()
    {
        var (factory, client) = await SetupAsync(
            (("thumb_enable", "true")),
            ("thumb_ffmpeg_path", Path.Combine(Path.GetTempPath(), "no-ffmpeg-here", "ffmpeg")));
        await using (factory)
        {
            await factory.WriteFileAsync("photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);

            var resp = await client.GetAsync("/p/test/photo.jpg?type=thumb");
            // 缺 ffmpeg 是部署问题，不是请求问题：明确 404 + 原因，前端退回图标即可。
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(resp.Headers.GetValues("X-Thumb-Reason").First()).IsEqualTo("ffmpeg-unavailable");
        }
    }

    [Test]
    public async Task Enabled_UnsupportedExtension_Returns415()
    {
        var (factory, client) = await SetupAsync(("thumb_enable", "true"));
        await using (factory)
        {
            await factory.WriteFileAsync("notes.txt", "hi"u8.ToArray());

            var resp = await client.GetAsync("/p/test/notes.txt?type=thumb");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
            await Assert.That(resp.Headers.GetValues("X-Thumb-Reason").First()).IsEqualTo("unsupported");
        }
    }

    [Test]
    public async Task Enabled_Head_Returns200WithoutGenerating()
    {
        var (factory, client) = await SetupAsync(("thumb_enable", "true"));
        await using (factory)
        {
            await factory.WriteFileAsync("photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);

            var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/p/test/photo.jpg?type=thumb"));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(resp.Content.Headers.ContentType!.MediaType).IsEqualTo("image/jpeg");
        }
    }

    [Test]
    public async Task Enabled_TooLargeSource_Returns404WithReason()
    {
        var (factory, client) = await SetupAsync((("thumb_enable", "true")), ("thumb_max_source_mb", "1"));
        await using (factory)
        {
            await factory.WriteFileAsync("big.jpg", new byte[2 * 1024 * 1024]);

            var resp = await client.GetAsync("/p/test/big.jpg?type=thumb");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(resp.Headers.GetValues("X-Thumb-Reason").First()).IsEqualTo("too-large");
        }
    }
}
