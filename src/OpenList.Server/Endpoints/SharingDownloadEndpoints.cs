using System.IO.Compression;
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
        endpoints.MapGet("/sad/{sid}", async (ISharingService sharing, IFileSystemService fs, HttpContext ctx, string sid) =>
            await HandleSharingArchiveDownload(sharing, fs, ctx, sid, null)).AllowAnonymous();
        endpoints.MapGet("/sad/{sid}/{*path}", async (ISharingService sharing, IFileSystemService fs, HttpContext ctx, string sid, string path) =>
            await HandleSharingArchiveDownload(sharing, fs, ctx, sid, path)).AllowAnonymous();

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

    private static async Task<IResult> HandleSharingArchiveDownload(ISharingService sharing, IFileSystemService fs, HttpContext ctx, string sid, string? path)
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

        var innerPath = ctx.Request.Query["inner"].ToString();
        var password = ctx.Request.Query["pass"].ToString();

        var link = await fs.LinkAsync(fullPath, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
        if (link == null) return Results.NotFound();

        // If no inner path, download the archive itself
        if (string.IsNullOrEmpty(innerPath))
        {
            if (link.URL.StartsWith("http")) return Results.Redirect(link.URL);
            if (System.IO.File.Exists(link.URL))
                return Results.File(link.URL, GetContentType(fullPath), System.IO.Path.GetFileName(fullPath));
            return Results.NotFound();
        }

        // Extract inner file from archive
        var extractedStream = await ExtractArchiveFile(link.URL, innerPath, password);
        if (extractedStream == null) return Results.NotFound();

        var fileName = System.IO.Path.GetFileName(innerPath);
        return Results.Stream(extractedStream, GetContentType(innerPath), fileName);
    }

    private static async Task<Stream?> ExtractArchiveFile(string? archivePath, string innerPath, string? password)
    {
        if (string.IsNullOrEmpty(archivePath) || !System.IO.File.Exists(archivePath)) return null;

        var ext = System.IO.Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext != ".zip") return null;

        try
        {
            var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(innerPath.TrimStart('/'));
            if (entry == null)
                entry = archive.Entries.FirstOrDefault(e => e.FullName == innerPath.TrimStart('/') || e.Name == System.IO.Path.GetFileName(innerPath));
            if (entry == null || entry.FullName.EndsWith('/'))
            {
                archive.Dispose();
                return null;
            }

            var ms = new MemoryStream();
            await entry.Open().CopyToAsync(ms);
            ms.Position = 0;
            archive.Dispose();
            return ms;
        }
        catch { return null; }
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
