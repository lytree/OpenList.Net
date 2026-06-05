using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

public static class SharingDownloadEndpoints
{
    public static IEndpointRouteBuilder MapSharingDownloadRoutes(this IEndpointRouteBuilder endpoints)
    {
        // /sd/:sid - sharing download
        endpoints.MapGet("/sd/{sid}", async (ISharingService sharing, IFileSystemService fs, HttpContext ctx, string sid) =>
            await HandleSharingDownload(sharing, fs, ctx, sid, null)).AllowAnonymous();
        endpoints.MapGet("/sd/{sid}/{*path}", async (ISharingService sharing, IFileSystemService fs, HttpContext ctx, string sid, string path) =>
            await HandleSharingDownload(sharing, fs, ctx, sid, path)).AllowAnonymous();
        endpoints.MapMethods("/sd/{sid}", ["HEAD"], async (ISharingService sharing, IFileSystemService fs, HttpContext ctx, string sid) =>
            await HandleSharingDownload(sharing, fs, ctx, sid, null)).AllowAnonymous();
        endpoints.MapMethods("/sd/{sid}/{*path}", ["HEAD"], async (ISharingService sharing, IFileSystemService fs, HttpContext ctx, string sid, string path) =>
            await HandleSharingDownload(sharing, fs, ctx, sid, path)).AllowAnonymous();

        // /sad/:sid - sharing archive download
        endpoints.MapGet("/sad/{sid}", (string sid) => Results.NotFound("not implemented")).AllowAnonymous();
        endpoints.MapGet("/sad/{sid}/{*path}", (string sid, string path) => Results.NotFound("not implemented")).AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> HandleSharingDownload(ISharingService sharing, IFileSystemService fs, HttpContext ctx, string sid, string? path)
    {
        var s = await sharing.GetSharingAsync(sid);
        if (s == null || s.Disabled)
            return Results.NotFound("sharing not found");

        if (!string.IsNullOrEmpty(s.Pwd))
        {
            if (!ctx.Request.Headers.TryGetValue("X-Sharing-Password", out var pwd) || pwd != s.Pwd)
                return Results.StatusCode(403);
        }

        var basePath = s.Files.FirstOrDefault() ?? "/";
        var fullPath = string.IsNullOrEmpty(path) ? basePath : $"{basePath}/{path}";

        var link = await fs.LinkAsync(fullPath, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
        if (link == null) return Results.NotFound();

        if (link.URL.StartsWith("http"))
            return Results.Redirect(link.URL);

        if (System.IO.File.Exists(link.URL))
            return Results.File(link.URL, GetContentType(fullPath), enableRangeProcessing: true);

        return Results.NotFound();
    }

    private static string GetContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4", ".mp3" => "audio/mpeg", ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}
