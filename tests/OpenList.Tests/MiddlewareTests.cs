using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenList.Core.Configuration;
using OpenList.Server.Middleware;

namespace OpenList.Tests;

/// <summary>
/// Phase 16 中间件单元测试：令牌桶限速器、限流路由判定、请求日志过滤、
/// 全局异常处理、最大并发闸门与启动期配置校验。
/// </summary>
public sealed class RateLimiterTests
{
    [Test]
    public async Task Unlimited_ReturnsImmediately_ForLargeBurst()
    {
        var limiter = new RateLimiter(0, 0); // 不限速
        await Assert.That(limiter.IsUnlimited).IsTrue();

        var sw = Stopwatch.StartNew();
        await limiter.WaitNAsync(1_000_000);
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(500);
    }

    [Test]
    public async Task NegativeRate_IsUnlimited()
    {
        var limiter = new RateLimiter(-1, 0); // Go 的 -1 表示无限
        await Assert.That(limiter.IsUnlimited).IsTrue();
        await limiter.WaitNAsync(10_000); // 不抛异常即通过
    }

    [Test]
    public async Task Burst_AllowsImmediateConsumption_DespiteSlowRate()
    {
        // 速率极慢（1 B/s）但突发容量 100：100 字节可直接消耗，无需等待。
        var limiter = new RateLimiter(1, 100);
        var sw = Stopwatch.StartNew();
        await limiter.WaitNAsync(100);
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(500);
    }

    [Test]
    public async Task Throttled_Wait_IsProportionalToRate()
    {
        // 512 B/s、突发 512；请求 1536 字节 → 超出突发部分按速率补令牌约 2s。
        var limiter = new RateLimiter(512, 0);
        var sw = Stopwatch.StartNew();
        await limiter.WaitNAsync(1536);
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(1500);
    }

    [Test]
    public async Task CancelledToken_ThrowsOperationCanceled()
    {
        var limiter = new RateLimiter(1, 1);
        await limiter.WaitNAsync(1); // 消耗掉唯一的突发令牌
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await limiter.WaitNAsync(100, cts.Token));
    }

    [Test]
    public async Task SetLimit_DowngradesBurst()
    {
        var limiter = new RateLimiter(10_000, 10_000);
        await limiter.WaitNAsync(5000);
        limiter.SetLimit(100, 10);
        await Assert.That(limiter.Tokens).IsLessThanOrEqualTo(10);
    }

    [Test]
    public async Task WriteStream_PassesDataThrough()
    {
        var data = "hello world"u8.ToArray();
        var ms = new MemoryStream();
        var limited = new RateLimitedWriteStream(ms, new RateLimiter(0, 0));
        await limited.WriteAsync(data);
        await limited.FlushAsync();
        await Assert.That(Convert.ToBase64String(ms.ToArray())).IsEqualTo(Convert.ToBase64String(data));
    }

    [Test]
    public async Task ReadStream_PassesDataThrough()
    {
        var data = "payload"u8.ToArray();
        var limited = new RateLimitedReadStream(new MemoryStream(data), new RateLimiter(0, 0));
        var buf = new byte[data.Length];
        var n = await limited.ReadAsync(buf);
        await Assert.That(n).IsEqualTo(data.Length);
        await Assert.That(Convert.ToBase64String(buf)).IsEqualTo(Convert.ToBase64String(data));
    }
}

/// <summary>
/// 下载/上传限流路由判定测试（对应 Go router.go 中 downloadLimiter/uploadLimiter 的挂载点）。
/// </summary>
public sealed class RateLimitRouteTests
{
    private static HttpRequest NewRequest(string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        return ctx.Request;
    }

    [Test]
    public async Task DownloadRoute_MatchesSignedAndSharingGetPaths()
    {
        foreach (var p in new[] { "/d/a", "/p/b", "/ad/c", "/ap/x", "/ae/y", "/sd/sid", "/sd/sid/sub", "/sad/sid" })
            await Assert.That(RateLimitMiddleware.IsDownloadRoute(NewRequest("GET", p))).IsTrue();
    }

    [Test]
    public async Task DownloadRoute_IgnoresHeadAndApiPaths()
    {
        await Assert.That(RateLimitMiddleware.IsDownloadRoute(NewRequest("HEAD", "/d/a"))).IsFalse();
        await Assert.That(RateLimitMiddleware.IsDownloadRoute(NewRequest("GET", "/api/fs/list"))).IsFalse();
        await Assert.That(RateLimitMiddleware.IsDownloadRoute(NewRequest("POST", "/d/a"))).IsFalse();
    }

    [Test]
    public async Task UploadRoute_MatchesPutUploadPaths()
    {
        foreach (var p in new[] { "/api/fs/put", "/api/fs/form", "/api/fs/multipart/chunk" })
            await Assert.That(RateLimitMiddleware.IsUploadRoute(NewRequest("PUT", p))).IsTrue();
    }

    [Test]
    public async Task UploadRoute_IgnoresNonPutAndOtherPaths()
    {
        await Assert.That(RateLimitMiddleware.IsUploadRoute(NewRequest("GET", "/api/fs/put"))).IsFalse();
        await Assert.That(RateLimitMiddleware.IsUploadRoute(NewRequest("PUT", "/api/fs/list"))).IsFalse();
    }
}

/// <summary>
/// 请求访问日志过滤测试（镜像 Go 的 skiperDecider 语义）。
/// </summary>
public sealed class RequestLogFilterTests
{
    private static RequestLogMiddleware NewMiddleware(LogFilterConfig filter) =>
        new(_ => Task.CompletedTask, NullLogger<RequestLogMiddleware>.Instance, filter);

    private static HttpRequest NewRequest(string method, string path, IPAddress? ip = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = ip ?? IPAddress.Parse("192.168.1.10");
        return ctx.Request;
    }

    [Test]
    public async Task NoFilters_OrFilterDisabled_DoesNotSkip()
    {
        await Assert.That(NewMiddleware(new LogFilterConfig()).ShouldSkip(NewRequest("GET", "/ping"))).IsFalse();
        await Assert.That(NewMiddleware(new LogFilterConfig
        {
            Enable = false,
            Filters = [new LogFilterItem { Path = "/ping" }],
        }).ShouldSkip(NewRequest("GET", "/ping"))).IsFalse();
    }

    [Test]
    public async Task PathPrefixFilter_Matches()
    {
        var filter = new LogFilterConfig { Enable = true, Filters = [new LogFilterItem { Path = "/ping" }] };
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("GET", "/ping"))).IsTrue();
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("GET", "/ping/extra"))).IsTrue();
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("GET", "/api/fs/list"))).IsFalse();
    }

    [Test]
    public async Task PathRelativeFilter_MatchesByContains()
    {
        // 不以 / 开头 → 按 "/path" 包含匹配（对应 Go 的相对路径匹配）。
        var filter = new LogFilterConfig { Enable = true, Filters = [new LogFilterItem { Path = "dav" }] };
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("PROPFIND", "/dav/"))).IsTrue();
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("GET", "/x"))).IsFalse();
    }

    [Test]
    public async Task MethodFilter_Matches()
    {
        var filter = new LogFilterConfig { Enable = true, Filters = [new LogFilterItem { Method = "HEAD" }] };
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("HEAD", "/d/a"))).IsTrue();
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("GET", "/d/a"))).IsFalse();
    }

    [Test]
    public async Task FilterConditions_AreAnd_AllMustMatch()
    {
        var filter = new LogFilterConfig
        {
            Enable = true,
            Filters = [new LogFilterItem { Path = "/dav/", Method = "PROPFIND" }],
        };
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("PROPFIND", "/dav/"))).IsTrue();
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("GET", "/dav/"))).IsFalse();
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("PROPFIND", "/other"))).IsFalse();
    }

    [Test]
    public async Task CidrFilter_Matches()
    {
        var filter = new LogFilterConfig
        {
            Enable = true,
            Filters = [new LogFilterItem { CIDR = "127.0.0.0/8" }],
        };
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("GET", "/x", IPAddress.Loopback))).IsTrue();
        await Assert.That(NewMiddleware(filter).ShouldSkip(NewRequest("GET", "/x", IPAddress.Parse("10.0.0.1")))).IsFalse();
    }

    [Test]
    public async Task CidrContains_HandlesPrefixAndFamilies()
    {
        await Assert.That(RequestLogMiddleware.CidrContains("127.0.0.1/32", "127.0.0.1")).IsTrue();
        await Assert.That(RequestLogMiddleware.CidrContains("127.0.0.1/32", "127.0.0.2")).IsFalse();
        await Assert.That(RequestLogMiddleware.CidrContains("192.168.1.0/24", "192.168.1.99")).IsTrue();
        await Assert.That(RequestLogMiddleware.CidrContains("192.168.1.0/24", "192.168.2.99")).IsFalse();
        await Assert.That(RequestLogMiddleware.CidrContains("10.0.0.1", "10.0.0.1")).IsTrue();   // 无前缀精确匹配
        await Assert.That(RequestLogMiddleware.CidrContains("not-a-cidr", "10.0.0.1")).IsFalse();
        await Assert.That(RequestLogMiddleware.CidrContains("127.0.0.0/8", "::1")).IsFalse();     // 地址族不一致
        await Assert.That(RequestLogMiddleware.CidrContains("::1/128", "::1")).IsTrue();          // IPv6
    }
}

/// <summary>
/// 全局异常处理中间件测试。
/// </summary>
public sealed class ErrorHandlingMiddlewareTests
{
    private static async Task<string> InvokeAndReadBodyAsync(Func<HttpContext, Task> next)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var mw = new ErrorHandlingMiddleware(
            nextCtx => next(nextCtx),
            NullLogger<ErrorHandlingMiddleware>.Instance);
        await mw.InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Test]
    public async Task UnhandledException_Returns500RespJson()
    {
        var body = await InvokeAndReadBodyAsync(_ => throw new InvalidOperationException("boom"));

        using var doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("code").GetInt32()).IsEqualTo(500);
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("internal server error");
    }

    [Test]
    public async Task BadHttpRequest_Returns400RespJson()
    {
        var body = await InvokeAndReadBodyAsync(_ =>
            throw new BadHttpRequestException("malformed"));

        using var doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("code").GetInt32()).IsEqualTo(400);
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("invalid request body");
    }

    [Test]
    public async Task SuccessfulRequest_PassesThrough()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var mw = new ErrorHandlingMiddleware(
            nextCtx => { nextCtx.Response.StatusCode = 201; return Task.CompletedTask; },
            NullLogger<ErrorHandlingMiddleware>.Instance);
        await mw.InvokeAsync(ctx);
        await Assert.That(ctx.Response.StatusCode).IsEqualTo(201);
    }
}

/// <summary>
/// 最大并发闸门中间件测试。
/// </summary>
public sealed class MaxAllowedMiddlewareTests
{
    [Test]
    public async Task AllowsSequentialRequests_UnderLimit()
    {
        var mw = new MaxAllowedMiddleware(_ => Task.CompletedTask, 2);
        for (var i = 0; i < 5; i++)
            await mw.InvokeAsync(new DefaultHttpContext());
        // 无死锁、无异常即通过
    }
}

/// <summary>
/// 启动期配置校验测试。
/// </summary>
public sealed class AppConfigurationValidationTests
{
    [Test]
    public async Task DefaultConfig_IsValid()
    {
        var config = new AppConfiguration();
        config.Validate(); // 不抛异常即通过
    }

    [Test]
    public async Task InvalidDatabaseType_Throws()
    {
        var config = new AppConfiguration();
        config.Database.Type = "oracle";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => config.Validate());
    }

    [Test]
    public async Task InvalidHttpPort_Throws()
    {
        var config = new AppConfiguration();
        config.Scheme.HttpPort = 70000;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => config.Validate());
    }

    [Test]
    public async Task NegativeMaxConnections_Throws()
    {
        var config = new AppConfiguration();
        config.MaxConnections = -1;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => config.Validate());
    }

    [Test]
    public async Task InvalidSearchIndex_Throws()
    {
        var config = new AppConfiguration();
        config.SearchIndex = "elastic";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => config.Validate());
    }
}
