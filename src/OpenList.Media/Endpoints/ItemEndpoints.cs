using System.Security.Claims;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>条目查询与元数据刮削端点。</summary>
internal static class ItemEndpoints
{
    /// <summary>挂载 <c>/items</c> 与整库刮削接口。</summary>
    internal static RouteGroupBuilder MapItemEndpoints(this RouteGroupBuilder g)
    {
        // GET /api/media/items?library_id=&page=&per_page=&status=&keyword=
        //                   &genre=&year_from=&year_to=&rating_min=&person_id=
        //                   &studio_id=&series_id=&sort=&desc=
        //                   &state=fresh|played|flagged|later|favorite|in-progress
        g.MapGet("/items", async (HttpContext ctx, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
        {
            var q = ctx.Request.Query;
            var query = new MediaItemQuery
            {
                LibraryId = MediaQuery.Int(q, "library_id", 0),
                Page = MediaQuery.Int(q, "page", 1),
                PerPage = MediaQuery.Int(q, "per_page", 30),
                Keyword = MediaQuery.Text(q, "keyword"),
                Status = MediaQuery.IntOrNull(q, "status") is { } st ? (MediaItemStatus)st : null,
                Genre = MediaQuery.Text(q, "genre"),
                YearFrom = MediaQuery.IntOrNull(q, "year_from"),
                YearTo = MediaQuery.IntOrNull(q, "year_to"),
                RatingMin = MediaQuery.DoubleOrNull(q, "rating_min"),
                PersonId = MediaQuery.LongOrNull(q, "person_id"),
                StudioId = MediaQuery.LongOrNull(q, "studio_id"),
                SeriesId = MediaQuery.LongOrNull(q, "series_id"),
                Sort = MediaQuery.Text(q, "sort"),
                Desc = MediaQuery.BoolOrNull(q, "desc"),
                State = MediaQuery.Text(q, "state"),
            };
            return Results.Ok(await libraries.ListItemsAsync(query, principal.ToUser()));
        });

        // GET /api/media/items/{id}
        g.MapGet("/items/{id:long}", async (long id, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
            Results.Ok(await libraries.GetItemAsync(id, principal.ToUser())));

        // GET /api/media/items/{id}/files
        g.MapGet("/items/{id:long}/files", async (long id, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
            Results.Ok(await libraries.ListItemFilesAsync(id, principal.ToUser())));

        // GET /api/media/items/{id}/image?kind=poster|backdrop
        //
        // 海报/背景图的专用读端点。前端网格**不能**自己拼 /d 链接或逐条 fs/get：
        // 一页 30 张海报就是 30 次带签名的元数据往返，而媒体页关心的只有「这张图」。
        // 可见性由 GetItemAsync 的 BasePath 过滤兜底 —— 拿不到条目就拿不到图。
        // 三种形态（404 / 302 外链 / 直读）见 MediaImages。
        g.MapGet("/items/{id:long}/image", async (
            long id,
            HttpContext ctx,
            ClaimsPrincipal principal,
            IMediaLibraryService libraries,
            IFileSystemService fs) =>
        {
            var user = principal.ToUser();
            var item = await libraries.GetItemAsync(id, user);
            if (item.Code != 200 || item.Data is null) return Results.NotFound();

            var kind = MediaQuery.Text(ctx.Request.Query, "kind") ?? "poster";
            var path = kind.Equals("backdrop", StringComparison.OrdinalIgnoreCase)
                ? item.Data.BackdropPath
                : item.Data.PosterPath;
            return await MediaImages.ServeAsync(path, user, fs, ctx.RequestAborted);
        });

        // GET /api/media/items/{id}/credits — 创作者列表
        g.MapGet("/items/{id:long}/credits", async (long id, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
            Results.Ok(await metadata.ListItemCreditsAsync(id, principal.ToUser())));

        // ---------- 刮削 ----------

        // POST /api/media/items/{id}/scrape — 自动刮削单条（?provider= 可选：tmdb 默认 / code 番号）
        g.MapPost("/items/{id:long}/scrape", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
        {
            var provider = MediaQuery.Text(ctx.Request.Query, "provider");
            return Results.Ok(await metadata.ScrapeItemAsync(id, principal.ToUser(), provider));
        });

        // POST /api/media/items/{id}/match?provider_id= — 人工指定匹配
        g.MapPost("/items/{id:long}/match", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
        {
            var providerId = ctx.Request.Query["provider_id"].ToString();
            return Results.Ok(await metadata.MatchItemAsync(id, providerId, principal.ToUser()));
        });

        // GET /api/media/scrape/tasks — 各库刮削状态（管理台「整库刮削」按钮的数据源）
        //
        // 与 GET /media/tasks（扫描任务）分开而不是合并：扫描状态回答「文件发现了多少」，
        // 刮削状态回答「元数据还差多少」，两者的生命周期与轮询用途都不同。
        // 路径挂 /scrape/tasks 而不是 /libraries/scrape，避免与 /libraries/{id} 系列混淆。
        g.MapGet("/scrape/tasks", async (ClaimsPrincipal principal, IMediaMetadataService metadata) =>
            Results.Ok(await metadata.ListScrapeTasksAsync(principal.ToUser())));

        // POST /api/media/libraries/{id}/scrape — 批量刮削整库（后台）
        g.MapPost("/libraries/{id:long}/scrape", async (long id, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
            Results.Ok(await metadata.ScrapeLibraryAsync(id, principal.ToUser())));

        return g;
    }
}
