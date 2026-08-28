namespace OpenList.Server.Middleware;

/// <summary>
/// 字节级令牌桶限速器，镜像 Go 的 <c>golang.org/x/time/rate.Limiter</c> 与
/// <c>internal/bootstrap/stream_limit.go</c> 的 <c>streamFilterNegative</c> 语义：
/// 速率以“每秒字节数”发放令牌，突发上限 <c>burst</c> 字节。
/// 值 <c>&lt;= 0</c> 视为不限速（端口约定：种子默认值为 0，且 Go 用 -1 表示无限，
/// 因此非正数一律不限制，避免存量库里的 0 把流量全部卡死）。
/// <see cref="WaitNAsync"/> 会阻塞直到足够令牌可用，可被 <see cref="CancellationToken"/> 取消。
/// </summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private double _ratePerSec; // 每秒令牌数；PositiveInfinity = 不限速
    private double _tokens;     // 当前可用令牌
    private int _burst;         // 令牌桶容量
    private long _lastRefillTicks;

    public RateLimiter() : this(0, 0)
    {
    }

    /// <param name="bytesPerSecond">每秒字节数；<c>&lt;= 0</c> 表示不限速。</param>
    /// <param name="burst">突发容量（字节），0 时自动取一秒钟的量。</param>
    public RateLimiter(int bytesPerSecond, int burst)
    {
        SetLimit(bytesPerSecond, burst);
        lock (_lock)
        {
            // Go 的 NewLimiter 初始令牌即为满桶（= 突发容量），立刻可用。
            if (!double.IsPositiveInfinity(_ratePerSec))
                _tokens = _burst;
        }
        _lastRefillTicks = Environment.TickCount64;
    }

    /// <summary>是否不限速（速率非正）。</summary>
    public bool IsUnlimited
    {
        get { lock (_lock) return double.IsPositiveInfinity(_ratePerSec); }
    }

    /// <summary>当前可用令牌数（诊断/测试用）。</summary>
    public double Tokens
    {
        get
        {
            lock (_lock)
            {
                RefillLocked();
                return _tokens;
            }
        }
    }

    /// <summary>
    /// 更新速率与突发容量。<paramref name="bytesPerSecond"/> 每秒字节数，<c>&lt;= 0</c> 不限速；
    /// <paramref name="burst"/> 为 0 时按一秒钟的速率自动取值。
    /// 从"从未限速"首次切换到有限速时直接补满突发容量（与 Go NewLimiter 满桶语义一致）。
    /// </summary>
    public void SetLimit(int bytesPerSecond, int burst)
    {
        lock (_lock)
        {
            // 从未限速（burst 为 0）意味着此前从未发放过令牌。
            var wasUnlimited = double.IsPositiveInfinity(_ratePerSec) || _burst <= 0;
            RefillLocked(); // Go SetLimit 内部先按流逝时间补令牌（advance）
            _ratePerSec = bytesPerSecond <= 0 ? double.PositiveInfinity : bytesPerSecond;
            var b = burst > 0 ? burst : (bytesPerSecond > 0 ? bytesPerSecond : 0);
            _burst = b;
            _tokens = wasUnlimited && !double.IsPositiveInfinity(_ratePerSec) && b > 0
                ? b
                : Math.Min(_tokens, _burst);
            _lastRefillTicks = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 等待直到可消费 <paramref name="n"/> 字节令牌。不限速时立即返回。
    /// 令牌可以透支为负值（债务），等待时长由负债务除以速率得出——这与 Go
    /// <c>rate.Limiter</c> 的 <c>reserveN</c> 语义一致，单个 <c>n</c> 可大于突发容量。
    /// 取消时抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async ValueTask WaitNAsync(int n, CancellationToken ct = default)
    {
        if (n <= 0) return;

        TimeSpan wait;
        lock (_lock)
        {
            RefillLocked();
            if (double.IsPositiveInfinity(_ratePerSec))
                return; // 不限速

            _tokens -= n;
            wait = _tokens >= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(-_tokens / _ratePerSec);
            if (wait > TimeSpan.Zero && wait < TimeSpan.FromMilliseconds(1))
                wait = TimeSpan.FromMilliseconds(1);
        }

        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
    }

    /// <summary>按流逝时间补充令牌（必须在持有锁的情况下调用）。正令牌上限为突发容量，负债务缓慢偿还。</summary>
    private void RefillLocked()
    {
        var now = Environment.TickCount64;
        var elapsedMs = now - _lastRefillTicks;
        if (elapsedMs <= 0) return;
        _lastRefillTicks = now;
        if (double.IsPositiveInfinity(_ratePerSec))
        {
            _tokens = _burst;
            return;
        }
        _tokens = Math.Min(_burst, _tokens + _ratePerSec * (elapsedMs / 1000.0));
    }
}

/// <summary>
/// 读取时按字节消耗令牌的流包装（对应 Go 的 <c>stream.RateLimitReader</c>），
/// 用于限制上传速率：包装 <c>HttpRequest.Body</c>。
/// </summary>
public sealed class RateLimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly RateLimiter _limiter;

    public RateLimitedReadStream(Stream inner, RateLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0) _limiter.WaitNAsync(n).AsTask().GetAwaiter().GetResult();
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken);
        if (n > 0) await _limiter.WaitNAsync(n, cancellationToken);
        return n;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// 写入时按字节消耗令牌的流包装（对应 Go 的 <c>stream.RateLimitWriter</c>），
/// 用于限制下载速率：包装 <c>HttpResponse.Body</c>。
/// </summary>
public sealed class RateLimitedWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly RateLimiter _limiter;

    public RateLimitedWriteStream(Stream inner, RateLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        if (count > 0) _limiter.WaitNAsync(count).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        if (!buffer.IsEmpty) await _limiter.WaitNAsync(buffer.Length, cancellationToken);
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => _inner.SetLength(value);
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
