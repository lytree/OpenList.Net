using System.Diagnostics;
using OpenList.Server.Middleware;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// 流量限速接线测试。
/// <para>
/// 背景：<see cref="RateLimitManager"/> 与四个令牌桶早已实现，但此前**没有任何调用点**，
/// 设置页里的 <c>max_client_download_speed</c> 等四个键配了等于没配。这组用例盯一件事：
/// 限速开启后内容仍然完整（不能因为扣令牌把响应截断）。
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
}
