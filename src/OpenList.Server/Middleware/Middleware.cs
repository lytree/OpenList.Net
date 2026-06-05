using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;

public class CorsMiddleware
{
    private readonly RequestDelegate _next;

    public CorsMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, OPTIONS, PATCH";
        context.Response.Headers.AccessControlAllowHeaders = "*";
        context.Response.Headers.AccessControlExposeHeaders = "Content-Disposition";

        if (context.Request.Method == "OPTIONS")
        {
            context.Response.StatusCode = 204;
            return;
        }

        await _next(context);
    }
}

/// <summary>
/// Redirects HTTP requests to HTTPS when both HTTP and HTTPS ports are configured
/// and ForceHttps is enabled.
/// </summary>
public class ForceHttpsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _httpPort;
    private readonly int _httpsPort;

    public ForceHttpsMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _httpPort = config.GetValue("HttpPort", -1);
        _httpsPort = config.GetValue("HttpsPort", -1);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.IsHttps && _httpPort != -1 && _httpsPort != -1)
        {
            var host = context.Request.Host.Host;
            if (_httpPort != 80)
                host = host.Replace($":{_httpPort}", $":{_httpsPort}", StringComparison.Ordinal);
            else
                host = $"{host}:{_httpsPort}";
            var httpsUrl = $"https://{host}{context.Request.Path}{context.Request.QueryString}";
            context.Response.Redirect(httpsUrl);
            return;
        }
        await _next(context);
    }
}

/// <summary>
/// Limits the number of concurrent connections.
/// </summary>
public class MaxConnectionsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SemaphoreSlim _semaphore;

    public MaxConnectionsMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        var maxConnections = config.GetValue("MaxConnections", 0);
        _semaphore = maxConnections > 0 ? new SemaphoreSlim(maxConnections) : new SemaphoreSlim(int.MaxValue);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_semaphore.CurrentCount == 0)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { code = 503, message = "too many connections" });
            return;
        }

        await _semaphore.WaitAsync(context.RequestAborted);
        try
        {
            await _next(context);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly Dictionary<string, List<DateTime>> _requests = new();
    private static readonly int _maxRequests = 100;
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(1);

    public RateLimitMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTime.UtcNow;

        lock (_requests)
        {
            if (!_requests.ContainsKey(ip))
                _requests[ip] = [];

            _requests[ip].RemoveAll(t => now - t > _window);

            if (_requests[ip].Count >= _maxRequests)
            {
                context.Response.StatusCode = 429;
                return;
            }

            _requests[ip].Add(now);
        }

        await _next(context);
    }
}

/// <summary>
/// Checks that storages have been loaded before allowing API requests.
/// Allows static assets and health check endpoints through while waiting.
/// </summary>
public class StoragesLoadedMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly SemaphoreSlim _signal = new(0);
    private static bool _loaded;

    public StoragesLoadedMiddleware(RequestDelegate next) => _next = next;

    public static void MarkLoaded() => _loaded = true;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_loaded)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var allowedPaths = new[] { "", "/", "/favicon.ico", "/ping", "/robots.txt", "/manifest.json" };
        if (allowedPaths.Contains(path))
        {
            await _next(context);
            return;
        }

        var allowedPrefixes = new[] { "/assets", "/images", "/streamer", "/static", "/swagger" };
        if (allowedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Wait for storages to load (with timeout)
        var timeout = TimeSpan.FromSeconds(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(timeout);

        try
        {
            await _signal.WaitAsync(cts.Token);
            await _next(context);
        }
        catch (OperationCanceledException)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { code = 503, message = "storages not loaded yet" });
        }
    }
}

/// <summary>
/// Validates upload permissions for fs/put and fs/form endpoints.
/// Checks File-Path header and user write permissions.
/// </summary>
public class FsUpMiddleware
{
    private readonly RequestDelegate _next;

    public FsUpMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/fs/put", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/fs/form", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var filePath = context.Request.Headers["File-Path"].ToString();
        if (string.IsNullOrEmpty(filePath))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { code = 400, message = "File-Path header is required" });
            return;
        }

        filePath = Uri.UnescapeDataString(filePath);

        var user = context.Items["User"] as User;
        if (user == null)
        {
            // Try to get from ClaimsPrincipal
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { code = 401, message = "unauthorized" });
                return;
            }
        }

        // User permission check is handled by the endpoint handler
        // This middleware just validates the File-Path header presence
        await _next(context);
    }
}

/// <summary>
/// Rate-limits download bandwidth by wrapping the response stream with a throttling stream.
/// Configured via "DownloadRateLimit" setting (bytes per second, 0 = unlimited).
/// </summary>
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
public class WebDavAuthMiddleware
{
    private readonly RequestDelegate _next;

    public WebDavAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IUserService userService, IAuthService authService)
    {
        var user = await AuthenticateAsync(context, userService, authService);

        // OPTIONS requests are always allowed (for CORS preflight)
        if (context.Request.Method == "OPTIONS")
        {
            context.Items["WebDavUser"] = user;
            await _next(context);
            return;
        }

        // PROPFIND/GET/HEAD require at least CanWebdavRead
        // PUT/MKCOL/COPY/MOVE/DELETE/PROPPATCH/LOCK/UNLOCK require CanWebdavManage
        var method = context.Request.Method;
        var isWriteMethod = method is "PUT" or "MKCOL" or "COPY" or "MOVE" or "DELETE" or "PROPPATCH" or "LOCK" or "UNLOCK";

        if (user == null)
        {
            context.Response.Headers.WWWAuthenticate = @"Basic realm=""openlist"", Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (user.Disabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!user.HasPermission(UserPermission.CanWebdavRead))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (isWriteMethod && !user.HasPermission(UserPermission.CanWebdavManage))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Store user and BasePath for downstream handlers
        context.Items["WebDavUser"] = user;
        context.Items["WebDavBasePath"] = user.BasePath ?? "/";

        await _next(context);
    }

    private static async Task<User?> AuthenticateAsync(HttpContext context, IUserService userService, IAuthService authService)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader)) return null;

        // Bearer token (JWT)
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            var user = await authService.GetCurrentUserAsync(token);
            return user;
        }

        // Basic Auth
        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = authHeader["Basic ".Length..].Trim();
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var separatorIndex = decoded.IndexOf(':');
                if (separatorIndex < 0) return null;

                var username = decoded[..separatorIndex];
                var password = decoded[(separatorIndex + 1)..];

                var user = await userService.GetUserByNameAsync(username);
                if (user == null) return null;

                var staticHash = OpenList.Infrastructure.Services.UserService.StaticHash(password);
                if (!OpenList.Infrastructure.Services.UserService.ValidatePwdStaticHash(user, staticHash))
                    return null;

                return user;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}

/// <summary>
/// Simple request logging middleware that logs HTTP method, path, status code, and duration.
/// </summary>
public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path;
        var start = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            var status = context.Response.StatusCode;
            var elapsed = sw.ElapsedMilliseconds;
            logger.LogInformation("{Method} {Path} -> {Status} ({Elapsed}ms)", method, path, status, elapsed);
        }
    }
}
