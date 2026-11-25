using System.Security.Claims;
using OpenList.Core.Security;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>
/// 关注 / 想看 / 下载三段端点。
/// <para>
/// **读**（更新流、关注列表、关注状态）与媒体门面同口径 <c>AllowAnonymous</c>：游客能看
/// 「谁在更新」，但私有库里的人物与条目不出现（服务层按库可见性过滤）。
/// **写**（关注、想看、提交下载）保持登录要求，由组级 <c>RequireAuthorization()</c> 兜住 ——
/// 这也是这三块里唯一不能对游客开放的部分：它们会真的去调 TMDB 与下载器。
/// </para>
/// </summary>
internal static class FollowEndpoints
{
    /// <summary>挂载关注、想看、下载端点。</summary>
    internal static RouteGroupBuilder MapFollowEndpoints(this RouteGroupBuilder g)
    {
        // ---------- 关注 ----------

        // GET /api/media/follow/state?person_ids=1,2,3
        g.MapGet("/follow/state", async (HttpContext ctx, ClaimsPrincipal principal, IMediaFollowService follow) =>
        {
            var ids = ParseIds(ctx.Request.Query["person_ids"]);
            return Results.Ok(await follow.GetStateAsync(ids, principal.ToUser(), ctx.RequestAborted));
        }).AllowAnonymous();

        // GET /api/media/follow — 已关注的创作者
        g.MapGet("/follow", async (ClaimsPrincipal principal, IMediaFollowService follow) =>
            Results.Ok(await follow.ListFollowedAsync(principal.ToUser()))).AllowAnonymous();

        // GET /api/media/follow/feed?page=&per_page=&person_id= — 新作品更新流
        g.MapGet("/follow/feed", async (HttpContext ctx, ClaimsPrincipal principal, IMediaFollowService follow) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            var personId = MediaQuery.LongOrNull(ctx.Request.Query, "person_id");
            return Results.Ok(await follow.GetFeedAsync(page, perPage, personId, principal.ToUser(), ctx.RequestAborted));
        }).AllowAnonymous();

        // POST /api/media/follow/check?person_id= — 检查更新（不给 person_id 就后台跑全部）
        g.MapPost("/follow/check", async (HttpContext ctx, ClaimsPrincipal principal, IMediaFollowService follow) =>
        {
            var personId = MediaQuery.LongOrNull(ctx.Request.Query, "person_id");
            return Results.Ok(await follow.CheckAsync(personId, principal.ToUser(), ctx.RequestAborted));
        });

        // POST /api/media/follow/{personId} — 关注（幂等）
        g.MapPost("/follow/{personId:long}", async (long personId, ClaimsPrincipal principal, IMediaFollowService follow) =>
            Results.Ok(await follow.FollowAsync(personId, principal.ToUser())));

        // DELETE /api/media/follow/{personId} — 取关（幂等）
        g.MapDelete("/follow/{personId:long}", async (long personId, ClaimsPrincipal principal, IMediaFollowService follow) =>
            Results.Ok(await follow.UnfollowAsync(personId, principal.ToUser())));

        // ---------- 想看 ----------

        // GET /api/media/wants?page=&per_page=&include_ingested=
        g.MapGet("/wants", async (HttpContext ctx, ClaimsPrincipal principal, IMediaWantService wants) =>
        {
            var (page, perPage) = MediaQuery.Paging(ctx.Request.Query);
            var includeIngested = MediaQuery.BoolOrNull(ctx.Request.Query, "include_ingested") ?? false;
            return Results.Ok(await wants.ListAsync(page, perPage, includeIngested, principal.ToUser(), ctx.RequestAborted));
        });

        // POST /api/media/wants — 加入想看（幂等）
        g.MapPost("/wants", async (AddMediaWantReq req, ClaimsPrincipal principal, IMediaWantService wants) =>
            Results.Ok(await wants.AddAsync(req, principal.ToUser())));

        // DELETE /api/media/wants/{id} — 移出想看（连同下载记录）
        g.MapDelete("/wants/{id:long}", async (long id, ClaimsPrincipal principal, IMediaWantService wants) =>
            Results.Ok(await wants.RemoveAsync(id, principal.ToUser())));

        // ---------- 下载 ----------

        // GET /api/media/downloads?want_id=
        g.MapGet("/downloads", async (HttpContext ctx, ClaimsPrincipal principal, IMediaDownloadService downloads) =>
        {
            var wantId = MediaQuery.LongOrNull(ctx.Request.Query, "want_id");
            return Results.Ok(await downloads.ListAsync(wantId, principal.ToUser(), ctx.RequestAborted));
        });

        // POST /api/media/downloads — 把资源地址交给离线下载，落到某个媒体库
        g.MapPost("/downloads", async (SubmitMediaDownloadReq req, ClaimsPrincipal principal, IMediaDownloadService downloads) =>
            Results.Ok(await downloads.SubmitAsync(req, principal.ToUser())));

        // DELETE /api/media/downloads/{id} — 取消下载
        g.MapDelete("/downloads/{id:long}", async (long id, ClaimsPrincipal principal, IMediaDownloadService downloads) =>
            Results.Ok(await downloads.CancelAsync(id, principal.ToUser())));

        return g;
    }

    /// <summary>解析 <c>person_ids=1,2,3</c>（也接受重复参数 <c>person_ids=1&amp;person_ids=2</c>）。</summary>
    private static List<long> ParseIds(string? raw)
    {
        var result = new List<long>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, out var id) && id > 0) result.Add(id);
        }
        return result;
    }
}
