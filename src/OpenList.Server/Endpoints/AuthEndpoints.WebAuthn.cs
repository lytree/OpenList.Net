using System.Security.Claims;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static partial class AuthEndpoints
{
    internal static void MapWebAuthnApi(IEndpointRouteBuilder endpoints)
    {
        var authn = endpoints.MapGroup("/api/authn");

        // Public: login endpoints
        authn.MapGet("webauthn_begin_login", async (WebAuthnService webAuthnService, string? username, CancellationToken ct) =>
        {
            var options = await webAuthnService.BeginLoginAsync(username, ct);
            if (!string.IsNullOrEmpty(username))
                webAuthnService.CacheAssertionOptions(username, options);
            return Results.Json(new Resp<object>(options));
        });

        authn.MapPost("webauthn_finish_login", async (WebAuthnService webAuthnService, HttpContext ctx, IUserService userService, IAuthService authService, string? username, CancellationToken ct) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<AuthenticatorAssertionRawResponse>(ct);
            if (body == null) return Results.Json(Resp<object>.Error(400, "invalid request"));
            if (string.IsNullOrEmpty(username)) return Results.Json(Resp<object>.Error(400, "username required"));

            var userId = await webAuthnService.FinishLoginAsync(username, body, ct);
            if (userId == null) return Results.Json(Resp<object>.Error(400, "authentication failed"));

            var user = await userService.GetUserByIdAsync(userId.Value, ct);
            if (user == null) return Results.Json(Resp<object>.Error(400, "user not found"));

            var token = authService.GenerateToken(user);
            return Results.Json(new Resp<object>(new { Token = token }));
        });

        // Authenticated: registration/management
        authn.MapGet("webauthn_begin_registration", async (WebAuthnService webAuthnService, ClaimsPrincipal user, string? credentialName, CancellationToken ct) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));

            var username = user.FindFirst(ClaimTypes.Name)?.Value ?? "";
            var options = await webAuthnService.BeginRegistrationAsync(userId.Value, username, credentialName, ct);
            webAuthnService.CacheRegistrationOptions(userId.Value, options);
            return Results.Json(new Resp<object>(options));
        }).RequireAuthorization();

        authn.MapPost("webauthn_finish_registration", async (WebAuthnService webAuthnService, HttpContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));

            var body = await ctx.Request.ReadFromJsonAsync<AuthenticatorAttestationRawResponse>(ct);
            if (body == null) return Results.Json(Resp<object>.Error(400, "invalid request"));

            var credentialName = ctx.Request.Query["name"].ToString();
            var credential = await webAuthnService.FinishRegistrationAsync(userId.Value, credentialName, body, ct);
            return Results.Json(new Resp<object>(credential));
        }).RequireAuthorization();

        authn.MapPost("delete_authn", async (WebAuthnService webAuthnService, ClaimsPrincipal user, int credentialId, CancellationToken ct) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));

            await webAuthnService.DeleteCredentialAsync(credentialId, userId.Value, ct);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        authn.MapGet("getcredentials", async (WebAuthnService webAuthnService, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));

            var credentials = await webAuthnService.GetCredentialsAsync(userId.Value, ct);
            return Results.Json(new Resp<object>(credentials));
        }).RequireAuthorization();
    }
}
