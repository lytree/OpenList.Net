using System.Security.Claims;
using OpenList.Core.Interfaces;
using OpenList.Core.Security;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>
/// 聚合查询端点：筛选条（facets）、统计（stats）、首页货架（home）、
/// 创作者（persons）与跨库扫描任务（tasks）。这些接口一次要扫全库分组，
/// 因此独立于条目列表服务（见 <see cref="IMediaCatalogService"/> 的说明）。
/// <para>
/// 浏览类接口（facets/stats/home/persons/studios/series）全部 <c>AllowAnonymous</c>：
/// 媒体库是站点的公开门面，游客不登录也要能看。可见范围仍由 <c>principal.ToUser()</c>
/// 决定 —— 匿名时是 BasePath=null 的 Guest（看全部），登录用户仍按自己的 BasePath 过滤。
/// </para>
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
        }).AllowAnonymous();

        // GET /api/media/stats?library_id= — 数量/体积/时长/年度与题材分布/评分分布
        g.MapGet("/stats", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var libraryId = MediaQuery.LongOrNull(ctx.Request.Query, "library_id");
            return Results.Ok(await catalog.GetStatsAsync(libraryId, principal.ToUser()));
        }).AllowAnonymous();

        // GET /api/media/home?limit=12 — 最近入库 / 继续观看 / 最近收藏
        // 匿名时「继续观看」「最近收藏」为空（个人状态表按 user_id=0 查不到记录）。
        g.MapGet("/home", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var limit = MediaQuery.Int(ctx.Request.Query, "limit", 12);
            return Results.Ok(await catalog.GetHomeAsync(limit, principal.ToUser()));
        }).AllowAnonymous();

        // GET /api/media/tasks — 跨库扫描任务（常驻任务卡）
        // 扫描任务属于管理面（管理台「任务」页在用），不对外开放。
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
        }).AllowAnonymous();

        // GET /api/media/persons/{id}/items — 该创作者的条目
        g.MapGet("/persons/{id:long}/items", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await catalog.ListPersonItemsAsync(id, page, perPage, principal.ToUser()));
        }).AllowAnonymous();

        // GET /api/media/persons/{id}
        g.MapGet("/persons/{id:long}", async (long id, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
            Results.Ok(await metadata.GetPersonAsync(id, principal.ToUser()))).AllowAnonymous();

        // GET /api/media/persons/{id}/image — 人物头像
        //
        // 与条目图同款（见 MediaImages）：TMDB 给的是外链 → 302；本地 NFO 的 <thumb>
        // 落到 VFS 路径 → 直读。没有头像（本地 NFO 常见）给 404，前端据此回退姓名首字。
        // 可见性靠 GetPersonAsync：它按「名下是否有可见条目」判定，越权同样 404。
        g.MapGet("/persons/{id:long}/image", async (
            long id,
            HttpContext ctx,
            ClaimsPrincipal principal,
            IMediaMetadataService metadata,
            IFileSystemService fs) =>
        {
            var user = principal.ToUser();
            var person = await metadata.GetPersonAsync(id, user, ctx.RequestAborted);
            if (person.Code != 200 || person.Data is null) return Results.NotFound();
            return await MediaImages.ServeAsync(person.Data.Person.ProfilePath, user, fs, ctx.RequestAborted);
        }).AllowAnonymous();

        // ---------- 片商/系列聚合（番号域） ----------

        // GET /api/media/studios?keyword=&page=&per_page= — 片商列表（按条目数降序）
        g.MapGet("/studios", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var q = ctx.Request.Query;
            var (page, perPage) = MediaQuery.Paging(q);
            return Results.Ok(await catalog.ListStudiosAsync(MediaQuery.Text(q, "keyword"), page, perPage, principal.ToUser()));
        }).AllowAnonymous();

        // GET /api/media/studios/{id} — 单条片商（详情页标题用，别再靠 query 传名字）
        g.MapGet("/studios/{id:long}", async (long id, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
            Results.Ok(await catalog.GetStudioAsync(id, principal.ToUser()))).AllowAnonymous();

        // GET /api/media/studios/{id}/items — 该片商名下的条目
        g.MapGet("/studios/{id:long}/items", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await catalog.ListStudioItemsAsync(id, page, perPage, principal.ToUser()));
        }).AllowAnonymous();

        // GET /api/media/series?keyword=&page=&per_page= — 系列列表（按条目数降序）
        g.MapGet("/series", async (HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var q = ctx.Request.Query;
            var (page, perPage) = MediaQuery.Paging(q);
            return Results.Ok(await catalog.ListSeriesAsync(MediaQuery.Text(q, "keyword"), page, perPage, principal.ToUser()));
        }).AllowAnonymous();

        // GET /api/media/series/{id}
        g.MapGet("/series/{id:long}", async (long id, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
            Results.Ok(await catalog.GetSeriesAsync(id, principal.ToUser()))).AllowAnonymous();

        // GET /api/media/series/{id}/items — 该系列名下的条目
        g.MapGet("/series/{id:long}/items", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaCatalogService catalog) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            return Results.Ok(await catalog.ListSeriesItemsAsync(id, page, perPage, principal.ToUser()));
        }).AllowAnonymous();

        return g;
    }
}
