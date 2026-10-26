using System.Security.Claims;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>
/// 播放列表端点。列表是<em>用户私有</em>的：所有操作都按 <c>UserId</c> 隔离，
/// 越权访问在服务层返回 403/404，而不是靠路由隐藏。
/// </summary>
internal static class PlaylistEndpoints
{
    /// <summary>挂载 <c>/playlists</c> 下的全部接口。</summary>
    internal static RouteGroupBuilder MapPlaylistEndpoints(this RouteGroupBuilder g)
    {
        // GET /api/media/playlists — 我的播放列表
        g.MapGet("/playlists", async (ClaimsPrincipal principal, IMediaPlaylistService playlists) =>
            Results.Ok(await playlists.ListAsync(principal.ToUser())));

        // POST /api/media/playlists — 新建 {name, description}
        g.MapPost("/playlists", async (ClaimsPrincipal principal, CreateMediaPlaylistReq req, IMediaPlaylistService playlists) =>
            Results.Ok(await playlists.CreateAsync(req, principal.ToUser())));

        // GET /api/media/playlists/{id} — 详情（含条目）
        g.MapGet("/playlists/{id:long}", async (long id, ClaimsPrincipal principal, IMediaPlaylistService playlists) =>
            Results.Ok(await playlists.GetAsync(id, principal.ToUser())));

        // POST /api/media/playlists/{id}/update — 改名 / 改描述
        g.MapPost("/playlists/{id:long}/update", async (long id, ClaimsPrincipal principal, UpdateMediaPlaylistReq req, IMediaPlaylistService playlists) =>
            Results.Ok(await playlists.UpdateAsync(id, req, principal.ToUser())));

        // POST /api/media/playlists/{id}/delete — 删除
        g.MapPost("/playlists/{id:long}/delete", async (long id, ClaimsPrincipal principal, IMediaPlaylistService playlists) =>
            Results.Ok(await playlists.DeleteAsync(id, principal.ToUser())));

        // POST /api/media/playlists/{id}/items — 追加条目 {item_id}
        g.MapPost("/playlists/{id:long}/items", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaPlaylistService playlists) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<PlaylistItemRef>(ctx.RequestAborted);
            if (body is null) return Results.BadRequest(new { message = "item_id is required" });
            return Results.Ok(await playlists.AddItemAsync(id, body.ItemId, principal.ToUser()));
        });

        // POST /api/media/playlists/{id}/items/remove — 移除条目 {item_id}
        g.MapPost("/playlists/{id:long}/items/remove", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaPlaylistService playlists) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<PlaylistItemRef>(ctx.RequestAborted);
            if (body is null) return Results.BadRequest(new { message = "item_id is required" });
            return Results.Ok(await playlists.RemoveItemAsync(id, body.ItemId, principal.ToUser()));
        });

        // POST /api/media/playlists/{id}/items/reorder — 重排 {item_ids:[...]}
        g.MapPost("/playlists/{id:long}/items/reorder", async (long id, ClaimsPrincipal principal, ReorderMediaPlaylistReq req, IMediaPlaylistService playlists) =>
            Results.Ok(await playlists.ReorderAsync(id, req.ItemIds, principal.ToUser())));

        // POST /api/media/playlists/add — 便捷入口：把条目加进指定列表，没有列表就新建
        // body: {item_id, playlist_id?, name?}
        g.MapPost("/playlists/add", async (HttpContext ctx, ClaimsPrincipal principal, IMediaPlaylistService playlists) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<AddToPlaylistReq>(ctx.RequestAborted);
            if (body is null || body.ItemId <= 0)
                return Results.BadRequest(new { message = "item_id is required" });

            var u = principal.ToUser();
            long playlistId;
            if (body.PlaylistId is > 0)
            {
                playlistId = body.PlaylistId.Value;
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(body.Name) ? "我的列表" : body.Name!.Trim();
                var created = await playlists.CreateAsync(new CreateMediaPlaylistReq { Name = name }, u);
                if (created.Code != RespCodes.Ok || created.Data is null) return Results.Ok(created);
                playlistId = created.Data.Id;
            }
            return Results.Ok(await playlists.AddItemAsync(playlistId, body.ItemId, u));
        });

        return g;
    }
}
