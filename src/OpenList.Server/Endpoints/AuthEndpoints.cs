using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Auth endpoints: login, 2FA, logout, current user, hash_password (admin).
/// Mirrors OpenList Go <c>server/handles/auth.go</c>.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginReq req, IAuthService auth) =>
        {
            var r = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        g.MapPost("/login/2fa", async (Login2FaReq req, IAuthService auth) =>
        {
            var r = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        g.MapPost("/logout", async (HttpContext ctx, IAuthService auth) =>
        {
            var token = ExtractToken(ctx);
            if (token is null) return Results.Ok(Resp.Fail("no token", RespCodes.Unauthorized));
            await auth.LogoutAsync(token);
            return Results.Ok(Resp.Ok());
        }).RequireAuthorization();

        g.MapGet("/me", async (ClaimsPrincipal user, IUserService users) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp<UserPublic>.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await users.GetCurrentAsync(id.Value);
            return Results.Ok(r);
        }).RequireAuthorization();

        g.MapPost("/2fa/generate", async (ClaimsPrincipal user, IAuthService auth) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp<Generate2FaResp>.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await auth.Generate2FaAsync(id.Value);
            if (r.Code != 200) return Results.Ok(r);
            var resp = new Generate2FaResp
            {
                Secret = r.Data!,
                QrUri = $"otpauth://totp/OpenList:{user.Identity?.Name}?secret={r.Data}&issuer=OpenList",
            };
            return Results.Ok(Resp<Generate2FaResp>.Ok(resp));
        }).RequireAuthorization();

        g.MapPost("/2fa/verify", async (ClaimsPrincipal user, Verify2FaReq req, IAuthService auth) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await auth.Verify2FaAsync(id.Value, req.Code);
            return Results.Ok(r);
        }).RequireAuthorization();

        g.MapPost("/hash_password", (HashPasswordReq req) =>
            Results.Ok(new HashPasswordResp { Hash = UserService.HashPassword(req.Password) }))
         .RequireAuthorization("admin");

        return app;
    }

    internal static string? ExtractToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth)) return null;
        // Frontend sends token WITHOUT "Bearer " prefix — accept both.
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : auth;
    }
}

/// <summary>Extension helpers for extracting user identity from <see cref="ClaimsPrincipal"/>.</summary>
public static class AuthClaimsExtensions
{
    public static long? UserId(this ClaimsPrincipal principal)
    {
        var v = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(v, out var id) ? id : null;
    }

    public static UserRole UserRole(this ClaimsPrincipal principal)
    {
        var v = principal.FindFirst(ClaimTypes.Role)?.Value;
        return int.TryParse(v, out var r) ? (UserRole)r : Core.Models.UserRole.Guest;
    }

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.UserRole() == Core.Models.UserRole.Admin;

    /// <summary>Build a minimal <see cref="User"/> from claims for authorization checks in services.</summary>
    public static User ToUser(this ClaimsPrincipal principal)
    {
        var id = principal.UserId() ?? 0;
        return new User
        {
            Id = id,
            Username = principal.Identity?.Name ?? string.Empty,
            Role = principal.UserRole(),
        };
    }
}
