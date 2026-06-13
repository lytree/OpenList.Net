using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;
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


