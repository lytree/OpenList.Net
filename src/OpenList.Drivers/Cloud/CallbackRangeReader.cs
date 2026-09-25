using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace OpenList.Drivers.Cloud;

/// <summary>
/// 阿里云盘代理下载「回调读端」：按 Range 向回调 URL 发起请求，并在整个响应体
/// 生命周期内持有 <see cref="CallbackLimiterRegistry"/> 的并发许可。
/// <para>
/// 对齐 Go <c>drivers/aliyundrive_open/callback.go</c> 的 <c>callbackRangeReader</c>：
/// </para>
/// <list type="bullet">
/// <item>仅对「确认是回调容量不足」的拒绝（HTTP 403 + <c>RequestDeniedByCallback</c> +
/// <c>ExceedMaxConcurrency</c>）做重试；其余错误立即失败，保留直连重定向与服务端限流语义。</item>
/// <item>重试最多 <see cref="CallbackLimiterRegistry.RequestAttempts"/> 次（默认 3），
/// 指数退避加抖动；用尽后抛 <see cref="CallbackCapacityException"/>，上层可映射为 SlowDown。</item>
/// <item>许可从获取起持有到响应流释放（读完/取消/关闭）为止，而不是响应头返回时就归还。</item>
/// </list>
/// </summary>
public sealed class CallbackRangeReader
{
    /// <summary>错误响应体最多读取的字节数，对齐 Go <c>callbackErrorBodyLimit</c>（64 KiB）。</summary>
    private const int ErrorBodyLimit = 64 << 10;

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly long _size;
    private readonly CallbackLimiterRegistration _registration;

    /// <summary>
    /// 构造回调读端。
    /// </summary>
    /// <param name="url">阿里云盘返回的回调下载 URL（Link 的原始直链）。</param>
    /// <param name="size">文件总大小（用于 Range 越界收敛与 200 响应切片）。</param>
    /// <param name="registration">驱动在 Init 时注册的准入句柄。</param>
    /// <param name="http">
    /// 可注入的 HTTP 客户端（测试用）；默认 <see cref="CloudHttpClient.Shared"/>。
    /// </param>
    public CallbackRangeReader(string url, long size, CallbackLimiterRegistration registration,
        HttpClient? http = null)
    {
        _url = url;
        _size = size;
        _registration = registration;
        _http = http ?? CloudHttpClient.Shared;
    }

    /// <summary>
    /// 判断响应是否为「回调容量不足」拒绝。对齐 Go <c>isCallbackCapacityRejection</c>：
    /// 必须同时满足 403、响应体含 <c>RequestDeniedByCallback</c> 与 <c>ExceedMaxConcurrency</c>。
    /// </summary>
    public static bool IsCapacityRejection(int statusCode, string body)
        => statusCode == 403
           && body.Contains("RequestDeniedByCallback", StringComparison.Ordinal)
           && body.Contains("ExceedMaxConcurrency", StringComparison.Ordinal);

    /// <summary>
    /// 打开 [start, start+length) 区间的响应流。许可在返回的流释放时才归还。
    /// </summary>
    /// <param name="start">起始偏移。</param>
    /// <param name="length">请求长度；&lt;0 或越界时收敛到文件末尾（对齐 Go 的 clamp）。</param>
    /// <param name="ct">取消令牌；取消会连带关闭已打开的响应流。</param>
    public async Task<Stream> OpenAsync(long start, long length, CancellationToken ct = default)
    {
        // 对齐 Go：requested.Length < 0 || Start+Length > size → 收敛到 size - Start。
        if (length < 0 || start + length > _size) length = _size - start;

        for (var attempt = 0; attempt < CallbackLimiterRegistry.RequestAttempts; attempt++)
        {
            var permit = await _registration.AcquireAsync(ct).ConfigureAwait(false);

            OpenResult opened;
            try
            {
                opened = await OpenCallbackRangeAsync(start, length, ct).ConfigureAwait(false);
            }
            catch
            {
                permit.Dispose();
                throw;
            }

            if (opened.Retry)
            {
                permit.Dispose();
                if (attempt + 1 == CallbackLimiterRegistry.RequestAttempts)
                {
                    throw new CallbackCapacityException(
                        $"Aliyun callback concurrency limit rejected {CallbackLimiterRegistry.RequestAttempts} attempts");
                }
                await DelayBeforeRetryAsync(attempt, ct).ConfigureAwait(false);
                continue;
            }

            if (opened.Error is not null)
            {
                permit.Dispose();
                throw opened.Error;
            }

            return opened.Body is null
                ? throw new InvalidOperationException("callback open returned neither body nor error")
                : new CallbackBodyStream(opened.Body, permit, ct);
        }

        // 循环恰好走满次数时不会到达；防御性兜底与 Go 的 "callback attempts exhausted" 对应。
        throw new CallbackCapacityException("callback attempts exhausted");
    }

    /// <summary>指数退避 + 抖动，对齐 Go <c>callbackRetryBaseDelay &lt;&lt; attempt</c> + 随机抖动。</summary>
    private static async Task DelayBeforeRetryAsync(int attempt, CancellationToken ct)
    {
        var delay = TimeSpan.FromTicks(CallbackLimiterRegistry.RetryBaseDelay.Ticks << attempt);
        var jitter = Random.Shared.NextInt64(Math.Max(1, delay.Ticks / 2));
        await Task.Delay(delay + TimeSpan.FromTicks(jitter), ct).ConfigureAwait(false);
    }

    /// <summary>发起一次回调 Range 请求并分类结果（成功体 / 需重试 / 硬错误）。</summary>
    private async Task<OpenResult> OpenCallbackRangeAsync(long start, long length, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _url);
        // Go 会合并调用方请求头（ProcessHeader）后应用 Range；.NET 侧尚无请求头透传管道，
        // 先只发 Range —— 与直连重定向语义不冲突。
        req.Headers.Range = length == long.MaxValue - start
            ? new RangeHeaderValue(start, null)
            : new RangeHeaderValue(start, start + length - 1);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return OpenResult.Fail(new InvalidOperationException($"Aliyun callback request failed: {e.Message}", e));
        }

        // 成功路径不释放 response —— 连接与响应体的所有权随 content 流移交给
        // <see cref="CallbackBodyStream"/>（释放内容流即可归还连接）；仅错误路径在此释放。
        if ((int)response.StatusCode >= 400)
        {
            string body;
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                body = await ReadLimitedAsync(stream, ErrorBodyLimit, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                response.Dispose();
                throw;
            }
            catch (Exception e)
            {
                response.Dispose();
                return OpenResult.Fail(new InvalidOperationException(
                    $"read Aliyun callback error response: {e.Message}", e));
            }

            using (response)
            {
                if (IsCapacityRejection((int)response.StatusCode, body))
                    return OpenResult.Retryable();

                // 错误消息里打码回调 URL（含签名参数），对齐 Go 的 <redacted>。
                var message = body.Trim().Replace(_url, "<redacted>", StringComparison.Ordinal);
                return OpenResult.Fail(new InvalidOperationException(
                    $"Aliyun callback request failed: HTTP {(int)response.StatusCode}; response: {message}"));
            }
        }

        var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var isFullRange = start == 0 && length >= _size;
        if (isFullRange || response.StatusCode == HttpStatusCode.PartialContent
            || ContentRangeStartsAt(response.Content.Headers.ContentRange, start))
        {
            // 响应体即请求区间（206 / 全量下载 / Content-Range 起点吻合）。
            return OpenResult.Success(content);
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            // 服务端忽略了 Range：本地切片跳过 start 并限长，对齐 Go GetRangedHttpReader。
            return OpenResult.Success(new SkipLimitStream(content, start, length));
        }

        return OpenResult.Success(content);
    }

    /// <summary>解析 Content-Range 的起始偏移并判断是否等于请求偏移（对齐 Go callbackContentRangeStartsAt）。</summary>
    private static bool ContentRangeStartsAt(ContentRangeHeaderValue? contentRange, long offset)
        => contentRange?.HasRange == true && contentRange.From == offset;

    /// <summary>最多读取 limit 字节并按 UTF-8 解码，超长截断（错误体只用于判定/日志）。</summary>
    private static async Task<string> ReadLimitedAsync(Stream stream, int limit, CancellationToken ct)
    {
        var buffer = new byte[limit];
        var read = 0;
        int n;
        while (read < limit && (n = await stream.ReadAsync(buffer.AsMemory(read, limit - read), ct).ConfigureAwait(false)) > 0)
        {
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private sealed record OpenResult(Stream? Body, bool Retry, Exception? Error)
    {
        public static OpenResult Success(Stream body) => new(body, false, null);
        public static OpenResult Retryable() => new(null, true, null);
        public static OpenResult Fail(Exception error) => new(null, false, error);
    }
}

/// <summary>
/// 绑定并发许可的回调响应体：释放时先关响应流再归还许可，均只执行一次；
/// 调用方取消时通过注册回调立刻关闭底层流（对齐 Go <c>newCallbackBody</c> 的
/// <c>context.AfterFunc</c>）。读取出错时自动释放（对齐 Go Read 的 Close-on-error）。
/// </summary>
internal sealed class CallbackBodyStream : Stream
{
    private readonly Stream _inner;
    private readonly CallbackPermit _permit;
    private readonly CancellationTokenRegistration _cancelReg;
    private int _disposed;

    internal CallbackBodyStream(Stream inner, CallbackPermit permit, CancellationToken ct)
    {
        _inner = inner;
        _permit = permit;
        // 取消时立即释放底层连接，避免取消后仍占用回调并发配额直到 GC。
        _cancelReg = ct.Register(static state => ((CallbackBodyStream)state!).DisposeInner(), this);
    }

    private void DisposeInner()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancelReg.Dispose();
        _inner.Dispose();
        _permit.Dispose();
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            var n = _inner.Read(buffer, offset, count);
            if (n == 0) DisposeInner(); // 读完即释放，尽早归还配额。
            return n;
        }
        catch
        {
            DisposeInner();
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) DisposeInner();
            return n;
        }
        catch
        {
            DisposeInner();
            throw;
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        DisposeInner();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        DisposeInner();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 服务端忽略 Range 头返回 200 全量时的本地切片流：首次读取跳过 <c>skip</c> 字节，
/// 之后最多再吐 <c>limit</c> 字节（对齐 Go <c>anet.GetRangedHttpReader</c> 的语义）。
/// </summary>
internal sealed class SkipLimitStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _skipBuffer = new byte[81920];
    private long _skipRemaining;
    private long _limitRemaining;
    private bool _skipped;

    internal SkipLimitStream(Stream inner, long skip, long limit)
    {
        _inner = inner;
        _skipRemaining = Math.Max(0, skip);
        _limitRemaining = limit;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private async ValueTask<int> SkipAsync(CancellationToken ct)
    {
        while (_skipRemaining > 0)
        {
            var n = await _inner.ReadAsync(
                _skipBuffer.AsMemory(0, (int)Math.Min(_skipBuffer.Length, _skipRemaining)), ct)
                .ConfigureAwait(false);
            if (n <= 0)
            {
                _skipRemaining = 0;
                _limitRemaining = 0;
                return 0; // 源提前结束。
            }
            _skipRemaining -= n;
        }
        _skipped = true;
        return 1;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (!_skipped && await SkipAsync(ct).ConfigureAwait(false) == 0) return 0;
        if (_limitRemaining <= 0) return 0;

        var n = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _limitRemaining)], ct)
            .ConfigureAwait(false);
        if (n > 0) _limitRemaining -= n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
