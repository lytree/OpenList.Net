using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Configuration;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using OpenList.Drivers.Cloud;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// 云驱动公共基础设施的测试：请求限流、OAuth 令牌刷新、Addition 回写通道、
/// 以及驱动运行时上下文注入。全部不依赖真实云盘服务。
/// </summary>
public class CloudDriverInfraTests
{
    // ------------------------------------------------------------------
    // RequestRateLimiter
    // ------------------------------------------------------------------

    [Test]
    public async Task RateLimiter_ZeroOrNegative_IsUnlimited()
    {
        await Assert.That(new RequestRateLimiter(0).IsUnlimited).IsTrue();
        await Assert.That(new RequestRateLimiter(-1).IsUnlimited).IsTrue();
        await Assert.That(new RequestRateLimiter(10).IsUnlimited).IsFalse();
    }

    [Test]
    public async Task RateLimiter_Unlimited_DoesNotDelay()
    {
        var limiter = new RequestRateLimiter(0);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) await limiter.WaitAsync();
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(500);
    }

    [Test]
    public async Task RateLimiter_FirstWait_IsImmediate()
    {
        // 令牌桶容量为 1：首个请求无需等待（对齐 Go rate.NewLimiter(rps, 1)）。
        var limiter = new RequestRateLimiter(1); // 间隔 1 秒
        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync();
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(200);
    }

    [Test]
    public async Task RateLimiter_Throttles_SubsequentWaits()
    {
        // 50 r/s → 20ms 间隔；5 次等待（首次瞬时）应至少耗时 4 × 20ms。
        var limiter = new RequestRateLimiter(50);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await limiter.WaitAsync();
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(70);
        // 上限只用于捕捉数量级异常的长等待（理论值 80ms）。阈值放宽到 5s：
        // 整个套件并行满载时 Task.Delay 的实际精度会显著劣化，1s 在 CI runner 上会偶发误报。
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
    }

    [Test]
    public async Task RateLimiter_ConcurrentWaits_AreSerialized()
    {
        var limiter = new RequestRateLimiter(50); // 20ms 间隔
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => limiter.WaitAsync()));
        sw.Stop();
        // 5 个并发请求也要按序排队，总耗时至少 4 个间隔。
        await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(70);
    }

    [Test]
    public async Task RateLimiter_Cancellation_Throws()
    {
        // 0.1 r/s → 10s 间隔：预消耗首个令牌后，下一次等待必然落在远超取消时限的未来。
        // 不能用 1 r/s——CI 满载时第一次等待的续延可能被调度延迟超过整个 interval，
        // 第二次等待算出 delay==0 直接放行，永远不会观察到取消令牌（已在 runner 上复现）。
        var limiter = new RequestRateLimiter(0.1);
        await limiter.WaitAsync(); // 消耗掉首个令牌
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await limiter.WaitAsync(cts.Token));
    }

    [Test]
    public async Task RateLimiter_PreCancelledToken_ThrowsImmediately()
    {
        // 已取消的令牌必须在进入等待前就抛出（对齐 Go limiter.Wait(ctx) 的语义），
        // 而不是碰巧拿到空闲时隙后静默成功。
        var limiter = new RequestRateLimiter(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await limiter.WaitAsync(cts.Token));
    }

    // ------------------------------------------------------------------
    // OAuthTokenRefresher
    // ------------------------------------------------------------------

    [Test]
    public async Task OAuthRefresher_Succeeds_OnFirstAttempt()
    {
        var calls = 0;
        var refresher = new OAuthTokenRefresher(_ =>
        {
            calls++;
            return Task.FromResult(new OAuthTokens("new-access", "new-refresh"));
        });

        var tokens = await refresher.RefreshAsync();
        await Assert.That(tokens.AccessToken).IsEqualTo("new-access");
        await Assert.That(tokens.RefreshToken).IsEqualTo("new-refresh");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task OAuthRefresher_Retries_UntilSuccess()
    {
        var calls = 0;
        var failures = new List<int>();
        var refresher = new OAuthTokenRefresher(_ =>
        {
            calls++;
            // 前两次失败，第三次成功 —— 模拟网络抖动。
            if (calls < 3) throw new HttpRequestException("boom");
            return Task.FromResult(new OAuthTokens("ok", "r"));
        })
        { OnFailure = (_, attempt) => failures.Add(attempt) };

        var tokens = await refresher.RefreshAsync();
        await Assert.That(tokens.AccessToken).IsEqualTo("ok");
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(failures).Count().IsEqualTo(2);
    }

    [Test]
    public async Task OAuthRefresher_ExhaustsAttempts_AndThrows()
    {
        var calls = 0;
        var refresher = new OAuthTokenRefresher(_ =>
        {
            calls++;
            throw new HttpRequestException("always fails");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await refresher.RefreshAsync());

        await Assert.That(calls).IsEqualTo(4); // 默认 4 次（1 + 3 次重试）
        await Assert.That(ex.Message).Contains("after 4 attempt");
        await Assert.That(ex.InnerException).IsNotNull();
    }

    [Test]
    public async Task OAuthRefresher_HonoursCustomMaxAttempts()
    {
        var calls = 0;
        var refresher = new OAuthTokenRefresher(_ =>
        {
            calls++;
            throw new HttpRequestException("nope");
        })
        { MaxAttempts = 2 };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await refresher.RefreshAsync());
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task OAuthRefresher_EmptyAccessToken_CountsAsFailure()
    {
        // 服务在 refresh_token 失效时可能返回 200 + 空 token：必须当失败处理，
        // 否则会把空 token 写回数据库。
        var calls = 0;
        var refresher = new OAuthTokenRefresher(_ =>
        {
            calls++;
            return Task.FromResult(new OAuthTokens("", "refresh"));
        })
        { MaxAttempts = 2 };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await refresher.RefreshAsync());
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task OAuthRefresher_Cancellation_IsNotRetried()
    {
        var calls = 0;
        using var cts = new CancellationTokenSource();
        var refresher = new OAuthTokenRefresher(ct =>
        {
            calls++;
            cts.Cancel();
            throw new OperationCanceledException(ct);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await refresher.RefreshAsync(cts.Token));
        await Assert.That(calls).IsEqualTo(1); // 取消不触发重试
    }

    [Test]
    public async Task OAuthRefresher_RejectsInvalidMaxAttempts()
    {
        var refresher = new OAuthTokenRefresher(_ => Task.FromResult(new OAuthTokens("a", "b")))
        { MaxAttempts = 0 };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await refresher.RefreshAsync());
    }

    [Test]
    public async Task OAuthRefresher_ForJsonEndpoint_ParsesTokens()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"acc","refresh_token":"ref"}""",
                Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);

        var refresher = OAuthTokenRefresher.ForJsonEndpoint(
            http, "https://api.example.com/token", () => new { refresh_token = "old" });

        var tokens = await refresher.RefreshAsync();
        await Assert.That(tokens.AccessToken).IsEqualTo("acc");
        await Assert.That(tokens.RefreshToken).IsEqualTo("ref");
        await Assert.That(handler.LastMethod).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.LastBody).Contains("old");
    }

    [Test]
    public async Task OAuthRefresher_ForJsonEndpoint_NonSuccess_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid_grant"),
        });
        using var http = new HttpClient(handler);

        var refresher = OAuthTokenRefresher.ForJsonEndpoint(
            http, "https://api.example.com/token", () => new { }, maxAttempts: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await refresher.RefreshAsync());
        await Assert.That(ex.Message).Contains("400");
    }

    [Test]
    public async Task OAuthRefresher_ForJsonEndpoint_InvalidJson_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>"),
        });
        using var http = new HttpClient(handler);

        var refresher = OAuthTokenRefresher.ForJsonEndpoint(
            http, "https://api.example.com/token", () => new { }, maxAttempts: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await refresher.RefreshAsync());
        await Assert.That(ex.Message).Contains("invalid JSON");
    }

    // ------------------------------------------------------------------
    // ZioDriverBase 运行时上下文 + Addition 回写
    // ------------------------------------------------------------------

    [Test]
    public async Task SaveAddition_WithoutSink_ReturnsFalse()
    {
        var driver = new FakeCloudDriver();
        var saved = await driver.SaveAdditionAsync(driver.Addition);
        await Assert.That(saved).IsFalse();
        await Assert.That(driver.MountPath).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SaveAddition_WithSink_SerializesByRuntimeType()
    {
        var sink = new RecordingSink();
        var driver = new FakeCloudDriver();
        driver.AttachRuntime("/ali", sink);

        var saved = await driver.SaveAdditionAsync(driver.Addition);

        await Assert.That(saved).IsTrue();
        await Assert.That(driver.MountPath).IsEqualTo("/ali");
        await Assert.That(sink.LastMountPath).IsEqualTo("/ali");
        // 必须按运行时类型序列化：JsonPropertyName 映射为 snake_case 键。
        await Assert.That(sink.LastAddition).Contains("access_token");
        await Assert.That(sink.LastAddition).Contains("acc-1");
        await Assert.That(sink.LastAddition).Contains("refresh_token");
    }

    [Test]
    public async Task SaveAddition_Null_ReturnsFalse()
    {
        var driver = new FakeCloudDriver();
        driver.AttachRuntime("/x", new RecordingSink());
        AliyundriveAdditionStub? nothing = null;
        await Assert.That(await driver.SaveAdditionAsync(nothing!)).IsFalse();
    }

    [Test]
    public async Task AttachRuntime_NullMountPath_BecomesEmpty()
    {
        var driver = new FakeCloudDriver();
        driver.AttachRuntime(null!, new RecordingSink());
        await Assert.That(driver.MountPath).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CloudDriverBase_ConfigureRateLimit_AppliesToWaits()
    {
        var driver = new FakeCloudDriver();
        driver.SetRate(0); // 显式不限流
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await driver.WaitAsync();
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(500);
    }

    // ------------------------------------------------------------------
    // StorageAdditionSink（真实落库）
    // ------------------------------------------------------------------

    [Test]
    public async Task StorageAdditionSink_WritesAdditionToDatabase()
    {
        var (provider, conn) = BuildProvider();
        await using var _ = conn;
        var sink = new StorageAdditionSink(provider.GetRequiredService<IServiceScopeFactory>());

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Storages.Add(new Storage { MountPath = "/ali", Driver = "AliyundriveOpen", Addition = """{"a":1}""" });
            await db.SaveChangesAsync();
        }

        var ok = await sink.SaveAdditionAsync("/ali", """{"a":2,"access_token":"tok"}""");
        await Assert.That(ok).IsTrue();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            var s = await db.Storages.SingleAsync(x => x.MountPath == "/ali");
            await Assert.That(s.Addition).IsEqualTo("""{"a":2,"access_token":"tok"}""");
            await Assert.That(s.Modified).IsGreaterThan(DateTimeOffset.MinValue);
            await Assert.That(s.UpdatedAt).IsEqualTo(s.Modified);
        }
    }

    [Test]
    public async Task StorageAdditionSink_UnknownMountPath_ReturnsFalse()
    {
        var (provider, conn) = BuildProvider();
        await using var _ = conn;
        var sink = new StorageAdditionSink(provider.GetRequiredService<IServiceScopeFactory>());

        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OpenListDbContext>().Database.EnsureCreatedAsync();

        await Assert.That(await sink.SaveAdditionAsync("/missing", "{}")).IsFalse();
    }

    [Test]
    public async Task StorageAdditionSink_UnchangedAddition_DoesNotTouchTimestamp()
    {
        var (provider, conn) = BuildProvider();
        await using var _ = conn;
        var sink = new StorageAdditionSink(provider.GetRequiredService<IServiceScopeFactory>());

        var stamp = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Storages.Add(new Storage
            {
                MountPath = "/ali",
                Driver = "AliyundriveOpen",
                Addition = "{}",
                Modified = stamp,
                UpdatedAt = stamp,
            });
            await db.SaveChangesAsync();
        }

        // 内容一致时必须短路，避免刷 token 时的无谓写放大。
        await Assert.That(await sink.SaveAdditionAsync("/ali", "{}")).IsTrue();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
            var s = await db.Storages.SingleAsync(x => x.MountPath == "/ali");
            await Assert.That(s.Modified).IsEqualTo(stamp);
        }
    }

    [Test]
    public async Task StorageAdditionSink_EmptyMountPath_ReturnsFalse()
    {
        var (provider, conn) = BuildProvider();
        await using var _ = conn;
        var sink = new StorageAdditionSink(provider.GetRequiredService<IServiceScopeFactory>());
        await Assert.That(await sink.SaveAdditionAsync("", "{}")).IsFalse();
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static (ServiceProvider Provider, SqliteConnection Connection) BuildProvider()
    {
        // 共享同一个打开的连接，作用域切换后内存库依然可见。
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var services = new ServiceCollection();
        // OpenListDbContext 的构造函数需要 DatabaseConfig（与 Program.cs 的注册方式一致）。
        services.AddSingleton(new DatabaseConfig());
        services.AddDbContext<OpenListDbContext>(o => o.UseSqlite(conn));
        return (services.BuildServiceProvider(), conn);
    }

    /// <summary>记录回写内容的假通道。</summary>
    private sealed class RecordingSink : IStorageAdditionSink
    {
        public string? LastMountPath { get; private set; }
        public string? LastAddition { get; private set; }

        public Task<bool> SaveAdditionAsync(string mountPath, string addition, CancellationToken ct = default)
        {
            LastMountPath = mountPath;
            LastAddition = addition;
            return Task.FromResult(true);
        }
    }

    /// <summary>可脚本化的 HTTP 响应处理器。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpMethod? LastMethod { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    /// <summary>假的云驱动，用来验证基类的回写与限流行为。</summary>
    private sealed class FakeCloudDriver : CloudDriverBase
    {
        public override string DriverName => "FakeCloud";
        public AliyundriveAdditionStub Addition { get; } = new() { AccessToken = "acc-1", RefreshToken = "ref-1" };
        protected override object AdditionObject => Addition;

        public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task WaitAsync(CancellationToken ct = default) => WaitRateLimitAsync(ct);
        public void SetRate(double rps) => ConfigureRateLimit(rps);
    }

    /// <summary>仅用于测试序列化的 Addition 形状。</summary>
    private sealed class AliyundriveAdditionStub
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;
    }
}
