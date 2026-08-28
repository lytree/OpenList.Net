using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 公开端点：站点设置、归档扩展名、离线下载工具列表（无需鉴权）。
/// 对应 OpenList Go <c>server/handles/public.go</c>。
/// </summary>
public static class PublicEndpoints
{
    /// <summary>
    /// 注册 <c>/api/public</c> 公开路由。各端点均接受 GET/POST 且匿名可访问。
    /// </summary>
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/public").WithTags("Public");

        // Go registers these via public.Any(...) — accept GET or POST.
        // GET|POST /api/public/settings — 返回公开设置项（site_title 等）
        g.MapMethods("/settings", ["GET", "POST"], async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetPublicAsync(ctx.RequestAborted)));

        // GET /api/public/archive_extensions — supported archive extensions
        g.MapMethods("/archive_extensions", ["GET", "POST"], async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetArchiveExtensionsAsync(ctx.RequestAborted)));

        // GET /api/public/offline_download_tools — enabled offline download tool names
        g.MapMethods("/offline_download_tools", ["GET", "POST"], async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetOfflineDownloadToolsAsync(ctx.RequestAborted)));

        return app;
    }
}
