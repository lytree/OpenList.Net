using System.Security.Claims;
using OpenList.Core.Security;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>
/// 媒体库数据清理端点：孤儿条目、重复文件、单条删除、清空回收站。
/// <para>
/// 全部**需要登录**（不开放 <c>AllowAnonymous</c>）：这里不只暴露路径，还会真的删文件。
/// 读接口（孤儿 / 重复）同样要登录 —— 它们列的是全库文件清单，不属于访客门面。
/// </para>
/// <para>
/// 「预览 → 执行」拆成两组端点：<c>GET</c> 出清单，<c>POST …/delete</c> 带 id 列表执行。
/// 执行侧对已消失的 id 一律跳过（扫描可能刚好清过），因此重复点、晚点都不报错。
/// </para>
/// </summary>
internal static class CleanupEndpoints
{
    /// <summary>挂载 <c>/cleanup</c> 下的清理接口。</summary>
    internal static RouteGroupBuilder MapCleanupEndpoints(this RouteGroupBuilder g)
    {
        // ---------- 孤儿条目 ----------

        // GET /api/media/cleanup/orphans?library_id=0 — 文件已全部消失的条目（0 = 全部可见库）
        g.MapGet("/cleanup/orphans", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCleanupService cleanup) =>
        {
            var libraryId = MediaQuery.LongOrNull(ctx.Request.Query, "library_id") ?? 0;
            return Results.Ok(await cleanup.ListOrphansAsync(libraryId, principal.ToUser(), ctx.RequestAborted));
        });

        // POST /api/media/cleanup/orphans/delete — 只删数据库行，不碰文件
        g.MapPost("/cleanup/orphans/delete", async (CleanupItemsReq req, ClaimsPrincipal principal, IMediaCleanupService cleanup) =>
            Results.Ok(await cleanup.DeleteOrphansAsync(req.ItemIds, principal.ToUser())));

        // ---------- 重复文件 ----------

        // GET /api/media/cleanup/duplicates?library_id=0 — 同尺寸同时长，再用前 1 MB 哈希确认
        g.MapGet("/cleanup/duplicates", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCleanupService cleanup) =>
        {
            var libraryId = MediaQuery.LongOrNull(ctx.Request.Query, "library_id") ?? 0;
            return Results.Ok(await cleanup.ListDuplicatesAsync(libraryId, principal.ToUser(), ctx.RequestAborted));
        });

        // POST /api/media/cleanup/duplicates/delete — 按库的回收站规则处理文件后删行
        g.MapPost("/cleanup/duplicates/delete", async (CleanupItemsReq req, ClaimsPrincipal principal, IMediaCleanupService cleanup) =>
            Results.Ok(await cleanup.DeleteDuplicatesAsync(req.ItemIds, principal.ToUser())));

        // ---------- 单条删除 ----------

        // POST /api/media/cleanup/items/{id}/delete  body: { "delete_files": true }
        g.MapPost("/cleanup/items/{id:long}/delete", async (long id, CleanupDeleteItemReq req, ClaimsPrincipal principal, IMediaCleanupService cleanup) =>
            Results.Ok(await cleanup.DeleteItemAsync(id, req.DeleteFiles, principal.ToUser())));

        // ---------- 回收站 ----------

        // POST /api/media/cleanup/trash/empty  body: { "library_id": 1 }
        g.MapPost("/cleanup/trash/empty", async (CleanupTrashReq req, ClaimsPrincipal principal, IMediaCleanupService cleanup) =>
            Results.Ok(await cleanup.EmptyTrashAsync(req.LibraryId, principal.ToUser())));

        return g;
    }
}
