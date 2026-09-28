using System.Net;
using OpenList.Core.Drivers;
using OpenList.Drivers.Cloud;
using OpenList.Drivers.Cloud.AliyundriveOpen;

namespace OpenList.Tests;

/// <summary>
/// 回调并发准入（对齐 Go drivers/aliyundrive_open/callback.go）的测试：
/// 按用户共享限额、许可生命周期、取消、超时容量错误，以及回调读端的重试分类。
/// <para>
/// 全部使用本地 Fake HttpMessageHandler，不访问真实云盘。
/// </para>
/// </summary>
public class CallbackConcurrencyTests
{
    /// <summary>把超时/退避调到毫秒级，避免测试拖慢；Dispose 时恢复。</summary>
    private sealed class FastTiming : IDisposable
    {
        public FastTiming()
        {
            CallbackLimiterRegistry.AcquireTimeout = TimeSpan.FromMilliseconds(100);
            CallbackLimiterRegistry.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        }

        public void Dispose()
        {
            CallbackLimiterRegistry.AcquireTimeout = TimeSpan.FromSeconds(1);
            CallbackLimiterRegistry.RetryBaseDelay = TimeSpan.FromMilliseconds(200);
        }
    }

    // --- 归一化 ---

    [Test]
    public async Task Normalize_NonPositive_FallsBackToDefault()
    {
        await Assert.That(CallbackLimiterRegistry.Normalize(0)).IsEqualTo(1);
        await Assert.That(CallbackLimiterRegistry.Normalize(-5)).IsEqualTo(1);
        await Assert.That(CallbackLimiterRegistry.Normalize(7)).IsEqualTo(7);
    }

    // --- 准入与许可生命周期 ---

    [Test]
    public async Task Acquire_RespectsLimit_AndReleaseFreesSlot()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-roundtrip", 2);

        var p1 = await reg.AcquireAsync(CancellationToken.None);
        var p2 = await reg.AcquireAsync(CancellationToken.None);

        // 配额 2 已满：再次准入应超时并抛临时容量异常。
        await Assert.ThrowsAsync<CallbackCapacityException>(
            async () => await reg.AcquireAsync(CancellationToken.None));

        p1.Dispose();
        var p3 = await reg.AcquireAsync(CancellationToken.None);
        p2.Dispose();
        p3.Dispose();
    }

    [Test]
    public async Task Acquire_SharesLimit_ByUserId()
    {
        using var _ = new FastTiming();
        // 同一用户两个存储：有效上限取最小值 1。
        using var regA = CallbackLimiterRegistry.Register("user-shared", 2);
        using var regB = CallbackLimiterRegistry.Register("user-shared", 1);

        using var p = await regA.AcquireAsync(CancellationToken.None);
        // regB 的 1 是全用户上限，regA 声明的 2 不生效。
        await Assert.ThrowsAsync<CallbackCapacityException>(
            async () => await regA.AcquireAsync(CancellationToken.None));
    }

    [Test]
    public async Task Acquire_DifferentUsers_AreIndependent()
    {
        using var _ = new FastTiming();
        using var regA = CallbackLimiterRegistry.Register("user-a", 1);
        using var regB = CallbackLimiterRegistry.Register("user-b", 1);

        using var pA = await regA.AcquireAsync(CancellationToken.None);
        using var pB = await regB.AcquireAsync(CancellationToken.None);
    }

    [Test]
    public async Task Acquire_CallerCancellation_ThrowsOce()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-cancel", 1);
        using var p = await reg.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reg.AcquireAsync(cts.Token));
    }

    [Test]
    public async Task Release_ThenUnregister_RemovesUserFromRegistry()
    {
        using var _ = new FastTiming();
        var reg = CallbackLimiterRegistry.Register("user-cleanup", 1);
        var p = await reg.AcquireAsync(CancellationToken.None);

        // 还有活动许可时注销：限流器必须保留。
        reg.Unregister();
        await Assert.That(CallbackLimiterRegistry.HasUser("user-cleanup")).IsTrue();

        p.Dispose();
        // 全部空闲且无注册后才回收。
        await Assert.That(CallbackLimiterRegistry.HasUser("user-cleanup")).IsFalse();
    }

    [Test]
    public async Task Unregister_IsIdempotent()
    {
        using var _ = new FastTiming();
        var reg = CallbackLimiterRegistry.Register("user-once", 1);
        reg.Unregister();
        reg.Unregister();
        await Assert.That(CallbackLimiterRegistry.HasUser("user-once")).IsFalse();
    }

    // --- 容量拒绝分类 ---

    [Test]
    public async Task IsCapacityRejection_MatchesOnlyVerifiedRejections()
    {
        await Assert.That(CallbackRangeReader.IsCapacityRejection(403,
                """{"code":"RequestDeniedByCallback","message":"ExceedMaxConcurrency"}""")).IsTrue();
        await Assert.That(CallbackRangeReader.IsCapacityRejection(403, "RequestDeniedByCallback only")).IsFalse();
        await Assert.That(CallbackRangeReader.IsCapacityRejection(403, "ExceedMaxConcurrency only")).IsFalse();
        await Assert.That(CallbackRangeReader.IsCapacityRejection(429, "ExceedMaxConcurrency RequestDeniedByCallback")).IsFalse();
    }

    // --- 回调读端（重试分类 + Range 语义） ---

    /// <summary>可编程的本地 HTTP 处理器：按队列返回响应，并记录收到的 Range 头。</summary>
    private sealed class FakeCallbackHandler : HttpMessageHandler
    {
        public readonly List<string?> RangeHeaders = [];
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = [];

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory) => _responses.Enqueue(factory);

        public void EnqueueContent(HttpStatusCode status, string body, string? contentRange = null)
            => Enqueue(_ =>
            {
                var resp = new HttpResponseMessage(status) { Content = new StringContent(body) };
                if (contentRange is not null) resp.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
                return resp;
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RangeHeaders.Add(request.Headers.Range?.ToString());
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private static string ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    [Test]
    public async Task OpenAsync_RetriesOnlyOnCapacityRejection_ThenSucceeds()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-reader-retry", 1);
        using var handler = new FakeCallbackHandler();
        handler.EnqueueContent(HttpStatusCode.Forbidden,
            """{"code":"RequestDeniedByCallback","message":"ExceedMaxConcurrency"}""");
        handler.EnqueueContent(HttpStatusCode.Forbidden,
            """{"code":"RequestDeniedByCallback","message":"ExceedMaxConcurrency"}""");
        handler.EnqueueContent(HttpStatusCode.PartialContent, "hello",
            contentRange: "bytes 0-4/5");
        var reader = new CallbackRangeReader("https://callback.example/file", 5, reg,
            new HttpClient(handler));

        await using var stream = await reader.OpenAsync(0, 5, CancellationToken.None);
        await Assert.That(ReadAll(stream)).IsEqualTo("hello");
        await Assert.That(handler.RangeHeaders).Count().IsEqualTo(3);
    }

    [Test]
    public async Task OpenAsync_NonRejectionError_FailsImmediately()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-reader-500", 1);
        using var handler = new FakeCallbackHandler();
        handler.EnqueueContent(HttpStatusCode.InternalServerError, "boom");
        var reader = new CallbackRangeReader("https://callback.example/secret?sign=x", 5, reg,
            new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reader.OpenAsync(0, 5, CancellationToken.None));
        // 错误消息打码回调 URL（含签名），不能泄露。
        await Assert.That(ex.Message).DoesNotContain("sign=x");
        await Assert.That(handler.RangeHeaders).Count().IsEqualTo(1);
    }

    [Test]
    public async Task OpenAsync_RejectionExhaustion_ThrowsCapacity()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-reader-exhaust", 1);
        using var handler = new FakeCallbackHandler();
        for (var i = 0; i < CallbackLimiterRegistry.RequestAttempts; i++)
        {
            handler.EnqueueContent(HttpStatusCode.Forbidden,
                """{"code":"RequestDeniedByCallback","message":"ExceedMaxConcurrency"}""");
        }
        var reader = new CallbackRangeReader("https://callback.example/file", 5, reg,
            new HttpClient(handler));

        await Assert.ThrowsAsync<CallbackCapacityException>(
            async () => await reader.OpenAsync(0, 5, CancellationToken.None));
        await Assert.That(handler.RangeHeaders).Count().IsEqualTo(CallbackLimiterRegistry.RequestAttempts);
    }

    [Test]
    public async Task OpenAsync_ClampsLengthToFileEnd()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-reader-clamp", 1);
        using var handler = new FakeCallbackHandler();
        handler.EnqueueContent(HttpStatusCode.PartialContent, "345", contentRange: "bytes 3-5/6");
        var reader = new CallbackRangeReader("https://callback.example/file", 6, reg,
            new HttpClient(handler));

        // 请求 [3, 3+10) 超出文件末尾：Range 应收敛为 bytes=3-5。
        await using var stream = await reader.OpenAsync(3, 10, CancellationToken.None);
        await Assert.That(ReadAll(stream)).IsEqualTo("345");
        await Assert.That(handler.RangeHeaders[0]).IsEqualTo("bytes=3-5");
    }

    [Test]
    public async Task OpenAsync_ServerIgnoresRange_SlicesLocally()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-reader-200", 1);
        using var handler = new FakeCallbackHandler();
        handler.EnqueueContent(HttpStatusCode.OK, "0123456789");
        var reader = new CallbackRangeReader("https://callback.example/file", 10, reg,
            new HttpClient(handler));

        // 服务端忽略 Range 返回 200 全量：本地切片应得到 [2, 5)。
        await using var stream = await reader.OpenAsync(2, 3, CancellationToken.None);
        await Assert.That(ReadAll(stream)).IsEqualTo("234");
    }

    [Test]
    public async Task OpenAsync_PermitHeldUntilStreamDisposed()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-reader-permit", 1);
        using var handler = new FakeCallbackHandler();
        handler.EnqueueContent(HttpStatusCode.PartialContent, "ab", contentRange: "bytes 0-1/2");
        var reader = new CallbackRangeReader("https://callback.example/file", 2, reg,
            new HttpClient(handler));

        var stream = await reader.OpenAsync(0, 2, CancellationToken.None);

        // 流未释放：唯一配额被占用，第二次准入必须等待超时。
        await Assert.ThrowsAsync<CallbackCapacityException>(
            async () => await reg.AcquireAsync(CancellationToken.None));

        await stream.DisposeAsync();

        // 流释放后配额归还。
        using var p = await reg.AcquireAsync(CancellationToken.None);
    }

    [Test]
    public async Task OpenAsync_CallerCancellation_Propagates()
    {
        using var _ = new FastTiming();
        using var reg = CallbackLimiterRegistry.Register("user-reader-cancel", 1);
        using var handler = new FakeCallbackHandler();
        var reader = new CallbackRangeReader("https://callback.example/file", 5, reg,
            new HttpClient(handler));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reader.OpenAsync(0, 5, cts.Token));
    }

    // --- 驱动接线 ---

    // 注意：callback_concurrency 归一化的用例已移到 AliyundriveOpenApiTests
    // （那里注入了 HTTP 桩）。这里不再直接调用 InitAsync —— 驱动移植后初始化会真实
    // 访问云盘 API，放在本文件里会让整个套件依赖网络。

    [Test]
    public async Task AttachCallbackLimiter_RegistersAndDisposesRelease()
    {
        using var _ = new FastTiming();
        // 不调用 InitAsync：那会真实访问云盘 API（getDriveInfo）。本用例只验证
        // 回调配额的「注册 / 重复注册 / 释放」登记行为，与初始化流程无关。
        var driver = new AliyundriveOpenDriver();

        driver.AttachCallbackLimiter("uid-123");
        await Assert.That(CallbackLimiterRegistry.HasUser("uid-123")).IsTrue();

        // 重复挂接：旧注册先注销，不留孤儿配额。
        driver.AttachCallbackLimiter("uid-123");
        await Assert.That(CallbackLimiterRegistry.HasUser("uid-123")).IsTrue();

        await driver.DisposeAsync();
        await Assert.That(CallbackLimiterRegistry.HasUser("uid-123")).IsFalse();
    }
}
