using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;
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

