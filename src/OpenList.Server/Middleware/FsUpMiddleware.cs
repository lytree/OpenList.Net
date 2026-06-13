using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;
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

