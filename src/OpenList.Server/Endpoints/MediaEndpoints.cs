using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 媒体库端点（Peach 功能的原生实现入口）。整体位于 <c>/api/media</c> 下，需登录。
/// 权限模型：库管理与扫描需要 <see cref="User.CanManageMediaLibrary"/>；
/// 浏览、收藏、口味画像等个人操作对所有可见用户开放，并按 <see cref="User.BasePath"/> 过滤。
/// </summary>
public static class MediaEndpoints
{
    /// <summary>注册 <c>/api/media</c> 路由组（需登录）。</summary>
    public static IEndpointRouteBuilder MapMediaApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/media").WithTags("Media").RequireAuthorization();

        // ---------- 媒体库 ----------

        // GET /api/media/libraries — 当前用户可见的媒体库
        g.MapGet("/libraries", async (ClaimsPrincipal principal, IMediaLibraryService libraries) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await libraries.ListAsync(u));
        });

        // POST /api/media/libraries — 创建媒体库
        g.MapPost("/libraries", async (ClaimsPrincipal principal, CreateMediaLibraryReq req, IMediaLibraryService libraries) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await libraries.CreateAsync(req, u));
        });

        // GET /api/media/libraries/{id}
        g.MapGet("/libraries/{id:long}", async (long id, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await libraries.GetAsync(id, u));
        });

        // POST /api/media/libraries/{id}/update
        g.MapPost("/libraries/{id:long}/update", async (long id, ClaimsPrincipal principal, UpdateMediaLibraryReq req, IMediaLibraryService libraries) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await libraries.UpdateAsync(id, req, u));
        });

        // POST /api/media/libraries/{id}/delete
        g.MapPost("/libraries/{id:long}/delete", async (long id, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await libraries.DeleteAsync(id, u));
        });

        // ---------- 扫描 ----------

        // POST /api/media/libraries/{id}/scan — 触发扫描
        g.MapPost("/libraries/{id:long}/scan", async (long id, ClaimsPrincipal principal, IMediaScanService scans) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await scans.StartAsync(id, u));
        });

        // POST /api/media/libraries/{id}/scan/stop — 停止扫描
        g.MapPost("/libraries/{id:long}/scan/stop", async (long id, ClaimsPrincipal principal, IMediaScanService scans) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await scans.StopAsync(id, u));
        });

        // GET /api/media/libraries/{id}/scan — 扫描进度
        g.MapGet("/libraries/{id:long}/scan", async (long id, IMediaScanService scans) =>
            Results.Ok(await scans.GetProgressAsync(id)));

        // GET /api/media/libraries/{id}/scan/logs?limit=20
        g.MapGet("/libraries/{id:long}/scan/logs", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaScanService scans) =>
        {
            var u = principal.ToUser();
            var limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? l : 20;
            return Results.Ok(await scans.ListLogsAsync(id, limit, u));
        });

        // ---------- 条目 ----------

        // GET /api/media/items?library_id=&page=&per_page=&status=&keyword=
        g.MapGet("/items", async (HttpContext ctx, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
        {
            var q = ctx.Request.Query;
            var query = new MediaItemQuery
            {
                LibraryId = long.TryParse(q["library_id"], out var libId) ? libId : 0,
                Page = int.TryParse(q["page"], out var page) ? page : 1,
                PerPage = int.TryParse(q["per_page"], out var perPage) ? perPage : 30,
                Keyword = q["keyword"].ToString(),
                Status = int.TryParse(q["status"], out var st) ? (MediaItemStatus)st : null,
            };
            var u = principal.ToUser();
            return Results.Ok(await libraries.ListItemsAsync(query, u));
        });

        // GET /api/media/items/{id}
        g.MapGet("/items/{id:long}", async (long id, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await libraries.GetItemAsync(id, u));
        });

        // GET /api/media/items/{id}/files
        g.MapGet("/items/{id:long}/files", async (long id, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await libraries.ListItemFilesAsync(id, u));
        });

        // GET /api/media/items/{id}/credits — 创作者列表
        g.MapGet("/items/{id:long}/credits", async (long id, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await metadata.ListItemCreditsAsync(id, u));
        });

        // ---------- 刮削 ----------

        // POST /api/media/items/{id}/scrape — 自动刮削单条
        g.MapPost("/items/{id:long}/scrape", async (long id, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await metadata.ScrapeItemAsync(id, u));
        });

        // POST /api/media/items/{id}/match?provider_id= — 人工指定匹配
        g.MapPost("/items/{id:long}/match", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
        {
            var providerId = ctx.Request.Query["provider_id"].ToString();
            var u = principal.ToUser();
            return Results.Ok(await metadata.MatchItemAsync(id, providerId, u));
        });

        // POST /api/media/libraries/{id}/scrape — 批量刮削整库（后台）
        g.MapPost("/libraries/{id:long}/scrape", async (long id, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await metadata.ScrapeLibraryAsync(id, u));
        });

        // ---------- 个人状态 ----------

        // GET /api/media/items/{id}/state
        g.MapGet("/items/{id:long}/state", async (long id, ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await states.GetAsync(id, u));
        });

        // POST /api/media/items/{id}/state — 收藏/看过/评分/进度
        g.MapPost("/items/{id:long}/state", async (long id, ClaimsPrincipal principal, UpdateUserMediaStateReq req, IUserMediaStateService states) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await states.UpdateAsync(id, req, u));
        });

        // GET /api/media/favorites?page=&per_page=
        g.MapGet("/favorites", async (HttpContext ctx, ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var page = int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1;
            var perPage = int.TryParse(ctx.Request.Query["per_page"], out var pp) ? pp : 30;
            var u = principal.ToUser();
            return Results.Ok(await states.ListFavoritesAsync(page, perPage, u));
        });

        // GET /api/media/in-progress — 在看
        g.MapGet("/in-progress", async (HttpContext ctx, ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var page = int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1;
            var perPage = int.TryParse(ctx.Request.Query["per_page"], out var pp) ? pp : 30;
            var u = principal.ToUser();
            return Results.Ok(await states.ListInProgressAsync(page, perPage, u));
        });

        // GET /api/media/taste — 口味画像
        g.MapGet("/taste", async (ClaimsPrincipal principal, IUserMediaStateService states) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await states.GetTasteProfileAsync(u));
        });

        // ---------- 创作者聚合 ----------

        // GET /api/media/persons/{id}
        g.MapGet("/persons/{id:long}", async (long id, ClaimsPrincipal principal, IMediaMetadataService metadata) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await metadata.GetPersonAsync(id, u));
        });

        // ---------- 字幕 ----------

        // GET /api/media/subtitles/search?title=&year=&language=
        g.MapGet("/subtitles/search", async (HttpContext ctx, ISubtitleService subtitles) =>
        {
            var q = ctx.Request.Query;
            var title = q["title"].ToString();
            if (string.IsNullOrWhiteSpace(title))
                return Results.Ok(Resp<List<SubtitleCandidate>>.Fail("title is required", RespCodes.BadRequest));
            var year = int.TryParse(q["year"], out var y) ? y : (int?)null;
            var language = q["language"].ToString();
            return Results.Ok(await subtitles.SearchAsync(title, year, string.IsNullOrWhiteSpace(language) ? "zh-cn" : language));
        });

        // POST /api/media/items/{id}/subtitles — 下载字幕并写回媒体目录
        g.MapPost("/items/{id:long}/subtitles", async (long id, ClaimsPrincipal principal, SubtitleCandidate candidate, ISubtitleService subtitles) =>
        {
            var u = principal.ToUser();
            return Results.Ok(await subtitles.DownloadToMediaAsync(id, candidate, u));
        });

        return app;
    }
}
