using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;
public class UploadRateLimiterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimiter? _limiter;

    public UploadRateLimiterMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        var bytesPerSecond = config.GetValue("UploadRateLimit", 0L);
        if (bytesPerSecond > 0)
        {
            _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = (int)Math.Min(bytesPerSecond, int.MaxValue),
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                TokensPerPeriod = (int)Math.Min(bytesPerSecond, int.MaxValue),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 100
            });
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_limiter == null)
        {
            await _next(context);
            return;
        }

        context.Request.Body = new RateLimitedStream(context.Request.Body, _limiter, context.RequestAborted);
        await _next(context);
    }
}

/// <summary>
/// A Stream wrapper that rate-limits write operations using a token bucket limiter.
/// </summary>
internal sealed class RateLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly RateLimiter _limiter;
    private readonly CancellationToken _ct;

    public RateLimitedStream(Stream inner, RateLimiter limiter, CancellationToken ct)
    {
        _inner = inner;
        _limiter = limiter;
        _ct = ct;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override async Task FlushAsync(CancellationToken ct) => await _inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => await _inner.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        var combinedCt = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct).Token;
        var written = 0;
        while (written < buffer.Length)
        {
            using var lease = await _limiter.AcquireAsync(1, combinedCt);
            if (!lease.IsAcquired) return;
            // Write one chunk per token acquired
            var chunkSize = Math.Min(buffer.Length - written, 8192);
            await _inner.WriteAsync(buffer.Slice(written, chunkSize), combinedCt);
            written += chunkSize;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// WebDAV authentication middleware supporting Basic Auth and Bearer token.
/// Validates user permissions (CanWebdavRead/CanWebdavManage) and BasePath restrictions.
/// </summary>

