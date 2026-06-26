using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Current-user endpoints: profile update, 2FA management, sessions.
/// Mirrors OpenList Go <c>server/handles/me.go</c>.
/// </summary>
public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me").WithTags("Me").RequireAuthorization();

        // GET /api/me — current user profile (frontend expects camelCase fields
        // matching the Go reference: id, username, password, base_path, role,
        // disabled, permission, sso_id, otp, allow_ldap).
        g.MapGet("/", async (ClaimsPrincipal user, IUserService users) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await users.GetCurrentAsync(id.Value);
            if (r.Code != 200) return Results.Ok(r);
            var u = r.Data!;
            return Results.Ok(Resp<object>.Ok(new
            {
                id = u.Id,
                username = u.Username,
                password = "",
                base_path = u.BasePath ?? "/",
                role = (int)u.Role,
                disabled = u.Disabled,
                permission = u.Permission,
                sso_id = u.SsoID ?? "",
                otp = u.TwoFactorEnabled,
                allow_ldap = u.AllowLdap,
            }));
        });

        g.MapPost("/update", async (ClaimsPrincipal user, UpdateMeReq req, IUserService users) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await users.UpdateCurrentAsync(id.Value, new UpdateUserReq
            {
                Username = req.Username,
                Password = req.Password,
                SsoID = req.SsoId,
            });
            return Results.Ok(r);
        });

        g.MapPost("/2fa", async (ClaimsPrincipal user, IAuthService auth) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await auth.Generate2FaAsync(id.Value);
            if (r.Code != 200) return Results.Ok(r);
            var resp = new Generate2FaResp
            {
                Secret = r.Data!,
                QrUri = $"otpauth://totp/OpenList:{user.Identity?.Name}?secret={r.Data}&issuer=OpenList",
            };
            return Results.Ok(Resp<Generate2FaResp>.Ok(resp));
        });

        // Sessions list/revoke — Phase 16 (requires persistent session store).
        g.MapGet("/sessions", () => Results.Ok(Resp<List<object>>.Ok([])));
        g.MapDelete("/sessions/{id}", (string id) => Results.Ok(Resp.Ok()));

        // --- SSH public keys ---
        // GET /api/me/sshkey/list — current user's keys (paged response shape:
        // { content, total } to match the frontend's PageResp<SSHPublicKey>).
        g.MapGet("/sshkey/list", async (ClaimsPrincipal user, ISshKeyService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await svc.ListAsync(id.Value);
            if (r.Code != 200) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data!, total = r.Data!.Count }));
        });

        // POST /api/me/sshkey/add — body { title, key }
        g.MapPost("/sshkey/add", async (ClaimsPrincipal user, SshKeyAddReq req, ISshKeyService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            return Results.Ok(await svc.AddAsync(id.Value, req.Title, req.Key));
        });

        // POST /api/me/sshkey/delete?id={id} — returns remaining keys
        g.MapPost("/sshkey/delete", async (HttpRequest http, ClaimsPrincipal user, ISshKeyService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            if (!long.TryParse(http.Query["id"], out var keyId))
                return Results.Ok(Resp.Fail("invalid id", RespCodes.BadRequest));
            // Non-admin: pass isAdmin=false so the service enforces ownership.
            return Results.Ok(await svc.DeleteAsync(id.Value, keyId, isAdmin: false));
        });

        // --- Messages ---
        g.MapGet("/message/unread", async (ClaimsPrincipal user, IMessageService msgs) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await msgs.GetAsync(id.Value, unreadOnly: true);
            return Results.Ok(Resp<int>.Ok(r.Data?.Count ?? 0));
        });

        g.MapGet("/message/list", async (ClaimsPrincipal user, IMessageService msgs) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            return Results.Ok(await msgs.GetAsync(id.Value));
        });

        g.MapPost("/message/read", async (ReadMessageReq req, ClaimsPrincipal user, IMessageService msgs) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            return Results.Ok(await msgs.MarkReadAsync(req.Id));
        });

        return app;
    }
}

public sealed class ReadMessageReq { public long Id { get; set; } }

public sealed class SshKeyAddReq
{
    public string Title { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}
