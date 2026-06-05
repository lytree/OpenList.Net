using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        // Public endpoints (no auth required)
        group.MapPost("login", async (IAuthService auth, LoginReq req) =>
        {
            var token = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            return token == null
                ? Results.Json(Resp<LoginResp>.Error(401, "login failed"), statusCode: 200)
                : Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        });

        group.MapPost("login/hash", async (IAuthService auth, LoginHashReq req) =>
        {
            var token = await auth.LoginHashAsync(req.Username, req.PasswordHash, req.OtpCode);
            return token == null
                ? Results.Json(Resp<LoginResp>.Error(401, "login failed"), statusCode: 200)
                : Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        });

        group.MapPost("login/ldap", async (IAuthService auth, LoginReq req) =>
        {
            var token = await auth.LoginLdapAsync(req.Username, req.Password);
            return token == null
                ? Results.Json(Resp<LoginResp>.Error(401, "ldap login failed"), statusCode: 200)
                : Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        });

        // SSO endpoints (public)
        group.MapGet("sso", (IAuthService auth, HttpContext ctx) =>
        {
            // SSO redirect - return redirect URL or error if not configured
            return Results.Json(Resp<object>.Error(501, "SSO not configured"));
        });

        group.MapGet("sso_callback", (IAuthService auth, HttpContext ctx) =>
        {
            return Results.Json(Resp<object>.Error(501, "SSO not configured"));
        });

        group.MapGet("get_sso_id", (IAuthService auth, HttpContext ctx) =>
        {
            return Results.Json(Resp<object>.Error(501, "SSO not configured"));
        });

        group.MapGet("sso_get_token", (IAuthService auth, HttpContext ctx) =>
        {
            return Results.Json(Resp<object>.Error(501, "SSO not configured"));
        });

        // Authenticated endpoints
        group.MapGet("logout", async (IAuthService auth, HttpContext ctx) =>
        {
            var token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
            if (!string.IsNullOrEmpty(token))
                await auth.InvalidateTokenAsync(token);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("2fa/generate", async (IAuthService auth, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<TwoFAResp>.Error(401, "unauthorized"));
            var (qr, secret) = await auth.Generate2FAAsync(userId.Value);
            return Results.Json(new Resp<TwoFAResp>(new TwoFAResp { QR = qr, Secret = secret }));
        }).RequireAuthorization();

        group.MapPost("2fa/verify", async (IAuthService auth, Verify2FAReq req, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var ok = await auth.Verify2FAAsync(userId.Value, req.Code, req.Secret);
            return ok
                ? Results.Json(new Resp<object>(null))
                : Results.Json(Resp<object>.Error(400, "verification failed"));
        }).RequireAuthorization();

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }
}
