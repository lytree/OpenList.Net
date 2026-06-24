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

        g.MapPost("/update", async (ClaimsPrincipal user, UpdateMeReq req, IUserService users) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await users.UpdateCurrentAsync(id.Value, new UpdateUserReq
            {
                Username = req.Username,
                Password = req.Password,
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
