using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace OpenList.Server.Middleware;

public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _jwtSecret;

    public AuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _jwtSecret = config["JwtSecret"] ?? "default-secret-key";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        // Skip auth for public endpoints
        if (path != null && (path.StartsWith("/api/auth/login") || path.StartsWith("/api/share/get") || path.StartsWith("/api/fs/list") || path.StartsWith("/swagger")))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
        {
            var token = authHeader[7..];
            try
            {
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
                var handler = new JwtSecurityTokenHandler();
                var principal = handler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.Zero
                }, out _);

                context.User = principal;
            }
            catch
            {
                // Invalid token - continue without auth
            }
        }

        await _next(context);
    }
}

public class SignMiddleware
{
    private readonly RequestDelegate _next;

    public SignMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path != null && (path.StartsWith("/d/") || path.StartsWith("/p/")))
        {
            var sign = context.Request.Query["sign"].ToString();
            if (!string.IsNullOrEmpty(sign))
            {
                // Sign verification would be done by the sign service
                // For now, just pass through
            }
        }
        await _next(context);
    }
}

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
