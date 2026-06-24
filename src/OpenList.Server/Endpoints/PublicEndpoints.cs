using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Public endpoints: settings + archive extensions (no auth required).
/// Mirrors OpenList Go <c>server/handles/public.go</c>.
/// </summary>
public static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/public").WithTags("Public");

        // GET /api/public/settings — public settings only (flag = Public)
        g.MapGet("/settings", async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetPublicAsync(ctx.RequestAborted)));

        // GET /api/public/archive_extensions — supported archive extensions
        g.MapGet("/archive_extensions", async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetArchiveExtensionsAsync(ctx.RequestAborted)));

        // GET /api/public/offline_download_tools — enabled offline download tool names
        g.MapGet("/offline_download_tools", async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetOfflineDownloadToolsAsync(ctx.RequestAborted)));

        return app;
    }
}
