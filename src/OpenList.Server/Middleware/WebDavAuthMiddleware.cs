using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using System.Threading.RateLimiting;

namespace OpenList.Server.Middleware;
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

