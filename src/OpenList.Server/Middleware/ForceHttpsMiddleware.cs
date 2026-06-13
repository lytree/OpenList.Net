using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;
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

