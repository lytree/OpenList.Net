using System.Security.Claims;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>字幕端点：按标题搜索候选、把选中字幕写回媒体目录。</summary>
internal static class SubtitleEndpoints
{
    /// <summary>挂载字幕相关接口。</summary>
    internal static RouteGroupBuilder MapSubtitleEndpoints(this RouteGroupBuilder g)
    {
        // GET /api/media/subtitles/search?title=&year=&language=
        g.MapGet("/subtitles/search", async (HttpContext ctx, ISubtitleService subtitles) =>
        {
            var q = ctx.Request.Query;
            var title = MediaQuery.Text(q, "title");
            if (title is null)
                return Results.Ok(Resp<List<SubtitleCandidate>>.Fail("title is required", RespCodes.BadRequest));

            var year = MediaQuery.IntOrNull(q, "year");
            var language = MediaQuery.Text(q, "language") ?? "zh-cn";
            return Results.Ok(await subtitles.SearchAsync(title, year, language));
        });

        // POST /api/media/items/{id}/subtitles — 下载字幕并写回媒体目录
        g.MapPost("/items/{id:long}/subtitles", async (long id, ClaimsPrincipal principal, SubtitleCandidate candidate, ISubtitleService subtitles) =>
            Results.Ok(await subtitles.DownloadToMediaAsync(id, candidate, principal.ToUser())));

        return g;
    }
}
