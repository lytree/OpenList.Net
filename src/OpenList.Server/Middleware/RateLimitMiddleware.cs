namespace OpenList.Server.Middleware;

/// <summary>限流中间件方向：下载（包装响应体）或上传（包装请求体）。</summary>
public enum RateLimitDirection
{
    Download,
    Upload,
}

/// <summary>
/// 下载/上传速率限制中间件，镜像 Go 的 <c>middlewares.DownloadRateLimiter</c> 与
/// <c>middlewares.UploadRateLimiter</c>：仅对匹配的下载/上传路由包装底层流，
/// 由 <see cref="RateLimitManager"/> 中的客户端限速器按字节消耗令牌。
/// 非匹配路径直接放行，零开销。
/// </summary>
public sealed class RateLimitMiddleware
{
    // Go router.go 中 downloadLimiter 挂载的签名/分享下载路由（GET）。
    private static readonly string[] DownloadPrefixes =
        { "/d/", "/p/", "/ad/", "/ap/", "/ae/", "/sd/", "/sad/" };

    // Go router.go 中 uploadLimiter 挂载的 PUT 上传路由。
    private static readonly string[] UploadPaths =
        { "/api/fs/put", "/api/fs/form", "/api/fs/multipart/chunk" };

    private readonly RequestDelegate _next;
    private readonly RateLimitManager _manager;
    private readonly RateLimitDirection _direction;

    public RateLimitMiddleware(RequestDelegate next, RateLimitManager manager, RateLimitDirection direction)
    {
        _next = next;
        _manager = manager;
        _direction = direction;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var request = ctx.Request;
        var isDownload = _direction == RateLimitDirection.Download;
        if ((isDownload && IsDownloadRoute(request)) || (!isDownload && IsUploadRoute(request)))
        {
            await _manager.EnsureFreshAsync(ctx.RequestAborted);
            var limiter = isDownload ? _manager.ClientDownload : _manager.ClientUpload;
            if (isDownload)
                ctx.Response.Body = new RateLimitedWriteStream(ctx.Response.Body, limiter);
            else
                ctx.Request.Body = new RateLimitedReadStream(ctx.Request.Body, limiter);
        }

        await _next(ctx);
    }

    /// <summary>判断请求是否为下载限流路由（GET /d/、/p/、/ad/、/ap/、/ae/、/sd/、/sad/）。</summary>
    public static bool IsDownloadRoute(HttpRequest request)
    {
        // HEAD 无响应体，无需限速（与 Go 只给 GET 挂 downloadLimiter 一致）。
        if (!HttpMethods.IsGet(request.Method)) return false;
        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var prefix in DownloadPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>判断请求是否为上传限流路由（PUT /api/fs/put|form|multipart/chunk）。</summary>
    public static bool IsUploadRoute(HttpRequest request)
    {
        if (!HttpMethods.IsPut(request.Method)) return false;
        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var upload in UploadPaths)
        {
            if (string.Equals(path, upload, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>
/// 最大并发连接数中间件，镜像 Go 的 <c>middlewares.MaxAllowed</c>：用信号量限制
/// 同时处理的请求数，超出时请求排队等待（不拒绝）。
/// </summary>
public sealed class MaxAllowedMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SemaphoreSlim _sem;

    public MaxAllowedMiddleware(RequestDelegate next, int maxConnections)
    {
        _next = next;
        _sem = new SemaphoreSlim(maxConnections, maxConnections);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        await _sem.WaitAsync(ctx.RequestAborted);
        try
        {
            await _next(ctx);
        }
        finally
        {
            _sem.Release();
        }
    }
}
