using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;
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

