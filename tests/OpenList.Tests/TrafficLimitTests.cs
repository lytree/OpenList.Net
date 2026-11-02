using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Server.Middleware;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// 流量限速与公开分享并发预算的接线测试。
/// <para>
/// 背景：<see cref="RateLimitManager"/> 与四个令牌桶早已实现，但此前**没有任何调用点**，
/// 设置页里的 <c>max_client_download_speed</c> 等四个键配了等于没配。这组用例盯两件事：
/// ① 限速开启后内容仍然完整（不能因为扣令牌把响应截断）；
/// ② 并发预算真的会挡住第二条流，并且额度会被归还（否则一次超限就永久 429）。
/// </para>
/// </summary>
[NotInParallel("WebIntegration")]
public class TrafficLimitTests
{
    // ---------- 流包装的单元行为 ----------

    [Test]
    public async Task WrapUpload_Is_Passthrough_When_Unlimited()
    {
        var body = new MemoryStream("payload"u8.ToArray());
        var wrapped = TrafficLimit.WrapUpload(body, new RateLimiter());
        await Assert.That(ReferenceEquals(wrapped, body)).IsTrue();
    }

    [Test]
    public async Task LimitRead_Is_Passthrough_When_Unlimited()
    {
        var inner = new MemoryStream("payload"u8.ToArray());
        var wrapped = TrafficLimit.LimitRead(inner, new RateLimiter());
        await Assert.That(ReferenceEquals(wrapped, inner)).IsTrue();
    }

    [Test]
    public async Task ThrottleAsync_Is_Noop_For_Null_Limiter()
    {
        var sw = Stopwatch.StartNew();
        await TrafficLimit.ThrottleAsync(null, 8 * 1024 * 1024);
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(200);
    }

    [Test]
    public async Task RateLimitedReadStream_Delays_After_Budget_Exhausted()
    {
        // 2 KB/s 速率、突发 2 KB：第一次读 1500 字节立刻放行，第二次读 1500 字节
        // 会把令牌打到 -1000，等待约 0.5 秒。断言取 250ms 下界，避开调度抖动。
        var limiter = new RateLimiter(2 * 1024, 0);
        var inner = new MemoryStream(new byte[4096]);
        await using var stream = new RateLimitedReadStream(inner, limiter);
        var buffer = new byte[1500];

        await stream.ReadAsync(buffer);
        var sw = Stopwatch.StartNew();
        await stream.ReadAsync(buffer);
        sw.Stop();

        await Assert.That(sw.ElapsedMilliseconds).IsGreaterThan(250);
    }

    // ---------- 端点接线 ----------

    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        return (factory, client);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var loginResp = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { username = "admin", password = "admin" }));
        var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonDocument>();
        var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    private static async Task<string> CreateShareAsync(HttpClient client, string path)
    {
        await LoginAsync(client);
        var resp = await client.PostAsync("/api/share/create", JsonContent.Create(new { files = new[] { path } }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    [Test]
    public async Task Sd_Delivers_Whole_Content_Under_Rate_Limit()
    {
        // 限速配置生效后内容必须完整：逐个字节扣令牌不能改变响应长度。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("hello.txt", "hello"u8.ToArray());
            await factory.SetSettingAsync("max_client_download_speed", "64");
            var code = await CreateShareAsync(client, "/test/hello.txt");

            client.DefaultRequestHeaders.Authorization = null;
            var resp = await client.GetAsync($"/sd/{code}");

            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo("hello");
        }
    }

    [Test]
    public async Task Sae_Delivers_Whole_Entry_Under_Rate_Limit()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.zip", TestWebAppFactory.CreateZipBytes());
            await factory.SetSettingAsync("max_server_download_speed", "64");
            var code = await CreateShareAsync(client, "/test/a.zip");

            client.DefaultRequestHeaders.Authorization = null;
            var resp = await client.GetAsync($"/sae/{code}?inner=hello.txt");

            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo("hello");
        }
    }

    [Test]
    public async Task Sd_Returns_429_When_Share_Concurrency_Exceeded_And_Recovers()
    {
        // 占满额度 → 429（带 Retry-After）；归还 → 立刻恢复 200。
        // 归还这条断言很关键：额度若挂在流的 Dispose 上而框架不释放，一次超限就会永久 429。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("hello.txt", "hello"u8.ToArray());
            var code = await CreateShareAsync(client, "/test/hello.txt");

            await factory.SetSettingAsync("share_stream_max_concurrency", "1");
            var budget = factory.Services.GetRequiredService<ShareStreamBudget>();
            // 首次调用读设置（内部有 10 秒缓存，所以先取一次再占额度）。
            await budget.EnsureFreshAsync();
            var lease = await budget.TryAcquireAsync(code);
            await Assert.That(lease).IsNotNull();

            client.DefaultRequestHeaders.Authorization = null;
            var blocked = await client.GetAsync($"/sd/{code}");
            await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(blocked.Headers.RetryAfter).IsNotNull();

            lease!.Dispose();
            var allowed = await client.GetAsync($"/sd/{code}");
            await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await allowed.Content.ReadAsStringAsync()).IsEqualTo("hello");
            await Assert.That(budget.InFlight(code)).IsEqualTo(0);
        }
    }
}
