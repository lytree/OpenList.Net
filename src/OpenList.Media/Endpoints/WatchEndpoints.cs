using System.Security.Claims;
using OpenList.Core.Security;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>
/// 观看会话端点：播放心跳上报、观看历史、逐日时长。
///
/// <para>
/// 上报是**幂等**的：同一个客户端 token 反复打过来只更新同一行。前端因此在
/// 起播时就能先发一次（顺便把"开始了"这件事记下来），不必等 10 秒后的第一次节流。
/// </para>
/// </summary>
internal static class WatchEndpoints
{
    /// <summary>挂载观看会话相关端点。</summary>
    internal static RouteGroupBuilder MapWatchEndpoints(this RouteGroupBuilder g)
    {
        // POST /api/media/watch/report — 心跳 / 结束（换文件、播完、关播放器）
        g.MapPost("/watch/report", async (ReportWatchReq req, ClaimsPrincipal principal, IWatchSessionService sessions) =>
            Results.Ok(await sessions.ReportAsync(req, principal.ToUser())));

        // GET /api/media/watch/history?days=30&limit=12 — 最近看过（按条目聚合）
        g.MapGet("/watch/history", async (HttpContext ctx, ClaimsPrincipal principal, IWatchSessionService sessions) =>
        {
            var days = MediaQuery.Int(ctx.Request.Query, "days", 30);
            var limit = MediaQuery.Int(ctx.Request.Query, "limit", 12);
            return Results.Ok(await sessions.ListHistoryAsync(days, limit, principal.ToUser()));
        });

        // GET /api/media/watch/stats?days=30 — 逐日观看时长（缺的日子补 0）
        g.MapGet("/watch/stats", async (HttpContext ctx, ClaimsPrincipal principal, IWatchSessionService sessions) =>
        {
            var days = MediaQuery.Int(ctx.Request.Query, "days", 30);
            return Results.Ok(await sessions.GetStatsAsync(days, principal.ToUser()));
        });

        return g;
    }
}
