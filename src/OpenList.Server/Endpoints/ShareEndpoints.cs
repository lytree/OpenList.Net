using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Sharing endpoints. Mirrors OpenList Go <c>server/handles/sharing.go</c>.
/// All endpoints require authentication. The frontend uses the user-visible
/// share code (string) for management lookups via <c>?id=</c>.
/// </summary>
public static class ShareEndpoints
{
    public static IEndpointRouteBuilder MapShareApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/share").WithTags("Share").RequireAuthorization();

        // GET /api/share/list — list shares owned by the user (admin: all).
        // Wrapped as { content, total } for the frontend Shares.tsx.
        g.MapGet("/list", async (ISharingService svc, ClaimsPrincipal user) =>
        {
            var r = await svc.ListAsync(user.UserId() ?? 0, user.IsAdmin());
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data, total = r.Data.Count }));
        });

        // GET /api/share/get?id={code} — get a single share by code.
        g.MapGet("/get", async (HttpContext ctx, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.GetAsync(code, user.UserId() ?? 0, user.IsAdmin()));
        });

        // POST /api/share/create — create a new share.
        g.MapPost("/create", async (ShareReq req, ISharingService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.CreateAsync(req, user.UserId() ?? 0, user.IsAdmin())));

        // POST /api/share/update?id={code} — update an existing share.
        g.MapPost("/update", async (HttpContext ctx, ShareReq req, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.UpdateAsync(code, req, user.UserId() ?? 0, user.IsAdmin()));
        });

        // POST /api/share/delete?id={code}
        g.MapPost("/delete", async (HttpContext ctx, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.DeleteAsync(code, user.UserId() ?? 0, user.IsAdmin()));
        });

        // POST /api/share/enable?id={code}
        g.MapPost("/enable", async (HttpContext ctx, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.EnableAsync(code, user.UserId() ?? 0, user.IsAdmin()));
        });

        // POST /api/share/disable?id={code}
        g.MapPost("/disable", async (HttpContext ctx, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.DisableAsync(code, user.UserId() ?? 0, user.IsAdmin()));
        });

        return app;
    }
}
