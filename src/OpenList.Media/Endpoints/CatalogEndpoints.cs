using System.Security.Claims;
using OpenList.Core.Security;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>
/// 聚合查询端点：筛选条（facets）、统计（stats）、首页货架（home）、
/// 创作者（persons）与跨库扫描任务（tasks）。这些接口一次要扫全库分组，
/// 因此独立于条目列表服务（见 <see cref="IMediaCatalogService"/> 的说明）。
/// </summary>
internal static class CatalogEndpoints
{
    /// <summary>挂载聚合类接口。</summary>
    internal static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder g)
    {
        // GET /api/media/facets?library_id= — 题材与年份聚合，筛选条数据源
        g.MapGet("/facets", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var libraryId = MediaQuery.LongOrNull(ctx.Request.Query, "library_id");
            return Results.Ok(await catalog.GetFacetsAsync(libraryId, principal.ToUser()));
        });

        // GET /api/media/stats?library_id= — 数量/体积/时长/年度与题材分布/评分分布
        g.MapGet("/stats", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var libraryId = MediaQuery.LongOrNull(ctx.Request.Query, "library_id");
            return Results.Ok(await catalog.GetStatsAsync(libraryId, principal.ToUser()));
        });

        // GET /api/media/home?limit=12 — 最近入库 / 继续观看 / 最近收藏
        g.MapGet("/home", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var limit = MediaQuery.Int(ctx.Request.Query, "limit", 12);
            return Results.Ok(await catalog.GetHomeAsync(limit, principal.ToUser()));
        });

        // GET /api/media/tasks — 跨库扫描任务（常驻任务卡）
        g.MapGet("/tasks", async (ClaimsPrincipal principal, IMediaScanService scans) =>
            Results.Ok(await scans.ListTasksAsync(principal.ToUser())));

        // ---------- 创作者聚合 ----------

        // GET /api/media/persons?keyword=&role=&page=&per_page= — 创作者列表（按条目数降序）
        g.MapGet("/persons", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var q = ctx.Request.Query;
            var (page, perPage) = MediaQuery.Paging(q);
            return Results.Ok(await catalog.ListPersonsAsync(
                MediaQuery.Text(q, "keyword"), MediaQuery.Text(q, "role"), page, perPage, principal.ToUser()));
        });

        // GET /api/media/persons/{id}/items — 该创作者的条目
        g.MapGet("/persons/{id:long}/items", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await catalog.ListPersonItemsAsync(id, page, perPage, principal.ToUser()));
        });

        // GET /api/media/persons/{id}
        g.MapGet("/persons/{id:long}", async (long id, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
            Results.Ok(await metadata.GetPersonAsync(id, principal.ToUser())));

        // ---------- 片商/系列聚合（番号域） ----------

        // GET /api/media/studios?keyword=&page=&per_page= — 片商列表（按条目数降序）
        g.MapGet("/studios", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var q = ctx.Request.Query;
            var (page, perPage) = MediaQuery.Paging(q);
            return Results.Ok(await catalog.ListStudiosAsync(MediaQuery.Text(q, "keyword"), page, perPage, principal.ToUser()));
        });

        // GET /api/media/studios/{id}/items — 该片商名下的条目
        g.MapGet("/studios/{id:long}/items", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await catalog.ListStudioItemsAsync(id, page, perPage, principal.ToUser()));
        });

        // GET /api/media/series?keyword=&page=&per_page= — 系列列表（按条目数降序）
        g.MapGet("/series", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var q = ctx.Request.Query;
            var (page, perPage) = MediaQuery.Paging(q);
            return Results.Ok(await catalog.ListSeriesAsync(MediaQuery.Text(q, "keyword"), page, perPage, principal.ToUser()));
        });

        // GET /api/media/series/{id}/items — 该系列名下的条目
        g.MapGet("/series/{id:long}/items", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await catalog.ListSeriesItemsAsync(id, page, perPage, principal.ToUser()));
        });

        return g;
    }
}
