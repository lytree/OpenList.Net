using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class ShareEndpoints
{
    public static IEndpointRouteBuilder MapShareApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/share");

        group.MapGet("list", async (ISharingService sharings, ClaimsPrincipal user, int page = 1, int perPage = 100) =>
        {
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            // Admin sees all, regular user sees own
            var list = await sharings.ListSharingsAsync(isAdmin ? 0 : userId ?? 0, page, perPage);
            return Results.Json(new Resp<PagedListResp<Sharing>>(new PagedListResp<Sharing> { Content = list, Total = list.Count }));
        });

        group.MapGet("get", async (ISharingService sharings, string id, string? pwd = null) =>
        {
            var sharing = await sharings.GetSharingAsync(id);
            if (sharing == null) return Results.Json(Resp<ShareGetResp>.Error(404, "share not found"));
            if (!sharing.Valid()) return Results.Json(Resp<ShareGetResp>.Error(403, "share expired or disabled"));
            if (!sharing.Verify(pwd)) return Results.Json(Resp<ShareGetResp>.Error(403, "wrong password"));

            sharing.Accessed++;
            await sharings.UpdateSharingAsync(sharing);

            return Results.Json(new Resp<ShareGetResp>(new ShareGetResp
            {
                Files = sharing.Files,
                Readme = sharing.Readme,
                Header = sharing.Header,
                OrderBy = sharing.OrderBy,
                OrderDirection = sharing.OrderDirection,
                ExtractFolder = sharing.ExtractFolder
            }));
        });

        group.MapPost("create", async (ISharingService sharings, ClaimsPrincipal user, CreateShareReq req) =>
        {
            var userId = GetUserId(user) ?? 0;
            var sharing = new Sharing
            {
                FilesRaw = req.FilesRaw, Pwd = req.Pwd, Expires = req.Expires,
                MaxAccessed = req.MaxAccessed, Remark = req.Remark, Readme = req.Readme,
                Header = req.Header, CreatorId = userId, OrderBy = req.OrderBy,
                OrderDirection = req.OrderDirection, ExtractFolder = req.ExtractFolder
            };
            return Results.Json(new Resp<Sharing>(await sharings.CreateSharingAsync(sharing)));
        }).RequireAuthorization();

        group.MapPost("update", async (ISharingService sharings, Sharing req) =>
        {
            await sharings.UpdateSharingAsync(req);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("delete", async (ISharingService sharings, DeleteShareReq req) =>
        {
            await sharings.DeleteSharingAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("enable", async (ISharingService sharings, DeleteShareReq req) =>
        {
            await sharings.EnableSharingAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("disable", async (ISharingService sharings, DeleteShareReq req) =>
        {
            await sharings.DisableSharingAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }
}
