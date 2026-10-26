using System.Security.Claims;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Endpoints;

/// <summary>媒体库管理与扫描端点。</summary>
internal static class LibraryEndpoints
{
    /// <summary>挂载 <c>/libraries</c> 下的库 CRUD 与扫描接口。</summary>
    internal static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder g)
    {
        // ---------- 媒体库 ----------

        // GET /api/media/libraries — 当前用户可见的媒体库
        g.MapGet("/libraries", async (ClaimsPrincipal principal, IMediaLibraryService libraries) =>
            Results.Ok(await libraries.ListAsync(principal.ToUser())));

        // POST /api/media/libraries — 创建媒体库
        g.MapPost("/libraries", async (ClaimsPrincipal principal, CreateMediaLibraryReq req, IMediaLibraryService libraries) =>
            Results.Ok(await libraries.CreateAsync(req, principal.ToUser())));

        // GET /api/media/libraries/{id}
        g.MapGet("/libraries/{id:long}", async (long id, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
            Results.Ok(await libraries.GetAsync(id, principal.ToUser())));

        // POST /api/media/libraries/{id}/update
        g.MapPost("/libraries/{id:long}/update", async (long id, ClaimsPrincipal principal, UpdateMediaLibraryReq req, IMediaLibraryService libraries) =>
            Results.Ok(await libraries.UpdateAsync(id, req, principal.ToUser())));

        // POST /api/media/libraries/{id}/delete
        g.MapPost("/libraries/{id:long}/delete", async (long id, ClaimsPrincipal principal, IMediaLibraryService libraries) =>
            Results.Ok(await libraries.DeleteAsync(id, principal.ToUser())));

        // ---------- 扫描 ----------

        // POST /api/media/libraries/{id}/scan — 触发扫描
        g.MapPost("/libraries/{id:long}/scan", async (long id, ClaimsPrincipal principal, IMediaScanService scans) =>
            Results.Ok(await scans.StartAsync(id, principal.ToUser())));

        // POST /api/media/libraries/{id}/scan/stop — 停止扫描
        g.MapPost("/libraries/{id:long}/scan/stop", async (long id, ClaimsPrincipal principal, IMediaScanService scans) =>
            Results.Ok(await scans.StopAsync(id, principal.ToUser())));

        // GET /api/media/libraries/{id}/scan — 扫描进度
        g.MapGet("/libraries/{id:long}/scan", async (long id, IMediaScanService scans) =>
            Results.Ok(await scans.GetProgressAsync(id)));

        // GET /api/media/libraries/{id}/scan/logs?limit=20
        g.MapGet("/libraries/{id:long}/scan/logs", async (long id, HttpContext ctx, ClaimsPrincipal principal, IMediaScanService scans) =>
        {
            var limit = MediaQuery.Int(ctx.Request.Query, "limit", 20);
            return Results.Ok(await scans.ListLogsAsync(id, limit, principal.ToUser()));
        });

        return g;
    }
}
