using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static partial class AuthEndpoints
{
    // Go: model.LoginCache - IP-based login attempt tracking
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _loginCache = new();
    private const int MaxAuthRetries = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        // Public endpoints (no auth required)
        group.MapPost("login", async (IAuthService auth, HttpContext ctx, [FromBody] LoginReq req) =>
        {
            // Go: check login attempt count by IP
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (_loginCache.TryGetValue(ip, out var count) && count >= MaxAuthRetries)
                return Results.Json(Resp<LoginResp>.Error(429, "Too many unsuccessful sign-in attempts have been made using an incorrect username or password, Try again later."));

            var token = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            if (token == "2FA_REQUIRED")
            {
                // Go: 2FA failure also increments login retry count
                _loginCache.AddOrUpdate(ip, 1, (_, v) => v + 1);
                return Results.Json(Resp<LoginResp>.Error(402, "2FA code required"));
            }
            if (token == null)
            {
                _loginCache.AddOrUpdate(ip, 1, (_, v) => v + 1);
                // Auto-expire cache entry
                _ = Task.Delay(LockDuration).ContinueWith(_ => _loginCache.TryRemove(ip, out var _));
                return Results.Json(Resp<LoginResp>.Error(401, "login failed"));
            }
            _loginCache.TryRemove(ip, out var _);
            return Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        }).WithSummary("用户登录（明文密码 + OTP）");

        group.MapPost("login/hash", async (IAuthService auth, HttpContext ctx, [FromBody] LoginHashReq req) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (_loginCache.TryGetValue(ip, out var count) && count >= MaxAuthRetries)
                return Results.Json(Resp<LoginResp>.Error(429, "Too many unsuccessful sign-in attempts have been made using an incorrect username or password, Try again later."));

            var token = await auth.LoginHashAsync(req.Username, req.PasswordHash, req.OtpCode);
            if (token == "2FA_REQUIRED")
            {
                // Go: 2FA failure also increments login retry count
                _loginCache.AddOrUpdate(ip, 1, (_, v) => v + 1);
                return Results.Json(Resp<LoginResp>.Error(402, "2FA code required"));
            }
            if (token == null)
            {
                _loginCache.AddOrUpdate(ip, 1, (_, v) => v + 1);
                _ = Task.Delay(LockDuration).ContinueWith(_ => _loginCache.TryRemove(ip, out var _));
                return Results.Json(Resp<LoginResp>.Error(401, "login failed"));
            }
            _loginCache.TryRemove(ip, out var _);
            return Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        }).WithSummary("用户登录（哈希密码 + OTP）");

        group.MapPost("login/ldap", async (IAuthService auth, HttpContext ctx, [FromBody] LoginReq req) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (_loginCache.TryGetValue(ip, out var count) && count >= MaxAuthRetries)
                return Results.Json(Resp<LoginResp>.Error(429, "Too many unsuccessful sign-in attempts have been made using an incorrect username or password, Try again later."));

            var token = await auth.LoginLdapAsync(req.Username, req.Password);
            if (token == null)
            {
                _loginCache.AddOrUpdate(ip, 1, (_, v) => v + 1);
                _ = Task.Delay(LockDuration).ContinueWith(_ => _loginCache.TryRemove(ip, out var _));
                return Results.Json(Resp<LoginResp>.Error(401, "ldap login failed"));
            }
            _loginCache.TryRemove(ip, out var _);
            return Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        }).WithSummary("LDAP 用户登录");

        // SSO endpoints (delegated to partial class)
        MapSsoApi(group);

        // WebAuthn endpoints (delegated to partial class)
        MapWebAuthnApi(endpoints);

        // Authenticated endpoints
        group.MapGet("logout", async (IAuthService auth, HttpContext ctx) =>
        {
            var token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
            if (!string.IsNullOrEmpty(token))
                await auth.InvalidateTokenAsync(token);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization().WithSummary("用户登出");

        group.MapPost("2fa/generate", async (IAuthService auth, IUserService users, ClaimsPrincipal user) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<TwoFAResp>.Error(401, "unauthorized"));
            // Go: guest cannot generate 2FA
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u != null && u.IsGuest) return Results.Json(Resp<TwoFAResp>.Error(403, "guest cannot generate 2FA"));
            var (qr, secret) = await auth.Generate2FAAsync(userId.Value);
            return Results.Json(new Resp<TwoFAResp>(new TwoFAResp { QR = qr, Secret = secret }));
        }).RequireAuthorization().WithSummary("生成 2FA 二维码和密钥");

        group.MapPost("2fa/verify", async (IAuthService auth, IUserService users, Verify2FAReq req, ClaimsPrincipal user) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            // Go: guest cannot verify 2FA
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u != null && u.IsGuest) return Results.Json(Resp<object>.Error(403, "guest cannot verify 2FA"));
            var ok = await auth.Verify2FAAsync(userId.Value, req.Code, req.Secret);
            return ok
                ? Results.Json(new Resp<object>(null))
                : Results.Json(Resp<object>.Error(400, "verification failed"));
        }).RequireAuthorization().WithSummary("验证 2FA 代码");

        return endpoints;
    }
}
