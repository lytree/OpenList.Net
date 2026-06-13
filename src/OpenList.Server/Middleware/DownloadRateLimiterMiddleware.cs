using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;
public class DownloadRateLimiterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimiter? _limiter;

    public DownloadRateLimiterMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        var bytesPerSecond = config.GetValue("DownloadRateLimit", 0L);
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

        var originalBody = context.Response.Body;
        context.Response.Body = new RateLimitedStream(originalBody, _limiter, context.RequestAborted);
        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }
}

/// <summary>
/// Rate-limits upload bandwidth by wrapping the request stream with a throttling stream.
/// Configured via "UploadRateLimit" setting (bytes per second, 0 = unlimited).
/// </summary>

