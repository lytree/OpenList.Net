using System.Security.Claims;
using OpenList.Core.Security;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>个人状态端点：收藏、看过、进度、稍后看、标记、口味画像。</summary>
internal static class StateEndpoints
{
    /// <summary>挂载条目状态与各类"个人视图"接口。</summary>
    internal static RouteGroupBuilder MapStateEndpoints(this RouteGroupBuilder g)
    {
        // GET /api/media/items/{id}/state
        g.MapGet("/items/{id:long}/state", async (long id, ClaimsPrincipal principal, IUserMediaStateService states) =>
            Results.Ok(await states.GetAsync(id, principal.ToUser())));

        // POST /api/media/items/states — 批量取个人状态（列表页/播放队列打「已看」角标）
        // 路由与下面的 `/items/{id:long}/state` 不可能撞：段数不同，且 id 约束是 long。
        g.MapPost("/items/states", async (BatchStatesReq req, ClaimsPrincipal principal, IUserMediaStateService states) =>
            Results.Ok(await states.GetStatesAsync(req.ItemIds, principal.ToUser())));

        // POST /api/media/items/{id}/state — 收藏/看过/评分/进度/稍后/标记
        g.MapPost("/items/{id:long}/state", async (long id, ClaimsPrincipal principal, UpdateUserMediaStateReq req, IUserMediaStateService states) =>
            Results.Ok(await states.UpdateAsync(id, req, principal.ToUser())));

        // GET /api/media/favorites?page=&per_page=
        g.MapGet("/favorites", async (HttpContext ctx, ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await states.ListFavoritesAsync(page, perPage, principal.ToUser()));
        });

        // GET /api/media/in-progress — 在看
        g.MapGet("/in-progress", async (HttpContext ctx, ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await states.ListInProgressAsync(page, perPage, principal.ToUser()));
        });

        // GET /api/media/taste?days= — 口味画像（days 为"最近 N 天内动过"的窗口，缺省=全量）
        g.MapGet("/taste", async (HttpContext ctx, ClaimsPrincipal principal, IUserMediaStateService states) =>
            Results.Ok(await states.GetTasteProfileAsync(
                principal.ToUser(), MediaQuery.IntOrNull(ctx.Request.Query, "days"))));

        // GET /api/media/watch-later — 稍后看
        g.MapGet("/watch-later", async (HttpContext ctx, ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await states.ListWatchLaterAsync(page, perPage, principal.ToUser()));
        });

        // GET /api/media/flagged — 已标记
        g.MapGet("/flagged", async (HttpContext ctx, ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await states.ListFlaggedAsync(page, perPage, principal.ToUser()));
        });

        // GET /api/media/unseen?library_id= — 未看（无看过记录）
        g.MapGet("/unseen", async (HttpContext ctx, ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            var libraryId = MediaQuery.LongOrNull(ctx.Request.Query, "library_id");
            return Results.Ok(await states.ListUnseenAsync(page, perPage, libraryId, principal.ToUser()));
        });

        return g;
    }
}
