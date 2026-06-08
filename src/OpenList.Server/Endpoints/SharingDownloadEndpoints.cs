using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

public static class SharingDownloadEndpoints
{
    public static IEndpointRouteBuilder MapSharingDownloadRoutes(this IEndpointRouteBuilder endpoints)
    {
        // /sd/:sid - sharing download
        endpoints.MapGet("/sd/{sid}", async (ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid) =>
            await HandleSharingDownload(sharing, fs, config, ctx, sid, null)).AllowAnonymous();
        endpoints.MapGet("/sd/{sid}/{*path}", async (ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid, string path) =>
            await HandleSharingDownload(sharing, fs, config, ctx, sid, path)).AllowAnonymous();
        endpoints.MapMethods("/sd/{sid}", ["HEAD"], async (ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid) =>
            await HandleSharingDownload(sharing, fs, config, ctx, sid, null)).AllowAnonymous();
        endpoints.MapMethods("/sd/{sid}/{*path}", ["HEAD"], async (ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid, string path) =>
            await HandleSharingDownload(sharing, fs, config, ctx, sid, path)).AllowAnonymous();

        // /sad/:sid - sharing archive download
        endpoints.MapGet("/sad/{sid}", async (ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid) =>
            await HandleSharingArchiveDownload(sharing, fs, config, ctx, sid, null)).AllowAnonymous();
        endpoints.MapGet("/sad/{sid}/{*path}", async (ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid, string path) =>
            await HandleSharingArchiveDownload(sharing, fs, config, ctx, sid, path)).AllowAnonymous();
        endpoints.MapMethods("/sad/{sid}", ["HEAD"], async (ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid) =>
            await HandleSharingArchiveDownload(sharing, fs, config, ctx, sid, null)).AllowAnonymous();
        endpoints.MapMethods("/sad/{sid}/{*path}", ["HEAD"], async (ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid, string path) =>
            await HandleSharingArchiveDownload(sharing, fs, config, ctx, sid, path)).AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> HandleSharingDownload(ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid, string? path)
    {
        var s = await sharing.GetSharingAsync(sid);
        if (s == null)
            return Results.NotFound("sharing not found");

        // Go: s.Valid() checks Disabled, MaxAccessed, Expires, Files, Creator
        if (s.Disabled)
            return Results.StatusCode(403);
        if (s.MaxAccessed > 0 && s.Accessed >= s.MaxAccessed)
            return Results.StatusCode(403);
        if (s.Files == null || s.Files.Count == 0)
            return Results.StatusCode(403);
        if (s.Expires.HasValue && s.Expires.Value < DateTime.UtcNow)
            return Results.StatusCode(403);

        if (!string.IsNullOrEmpty(s.Pwd))
        {
            // Go: uses query param "pwd"
            var pwd = ctx.Request.Query["pwd"].ToString();
            if (string.IsNullOrEmpty(pwd))
            {
                // Also support header for compatibility
                ctx.Request.Headers.TryGetValue("X-Sharing-Password", out var headerPwd);
                pwd = headerPwd.ToString();
            }
            if (pwd != s.Pwd)
                return Results.StatusCode(403);
        }

        // Go: cannot get sharing root link if multiple files
        var basePath = s.Files.FirstOrDefault() ?? "/";
        if (s.Files.Count != 1 && string.IsNullOrEmpty(path))
            return Results.StatusCode(403);

        var fullPath = string.IsNullOrEmpty(path) ? basePath : $"{basePath.TrimEnd('/')}/{path}";

        // Go: countAccess - increment accessed count per unique IP
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
        await sharing.IncrementAccessAsync(sid, ip);

        var link = await fs.LinkAsync(fullPath, new LinkArgs { IP = ip });
        if (link == null) return Results.NotFound();

        if (link.URL.StartsWith("http"))
            return Results.Redirect(link.URL);

        if (System.IO.File.Exists(link.URL))
            return Results.File(link.URL, GetContentType(fullPath), enableRangeProcessing: true);

        return Results.NotFound();
    }

    private static async Task<IResult> HandleSharingArchiveDownload(ISharingService sharing, IFileSystemService fs, IConfiguration config, HttpContext ctx, string sid, string? path)
    {
        // Go: checks ShareArchivePreview setting
        if (!config.GetValue("ShareArchivePreview", true))
            return Results.StatusCode(403);

        var s = await sharing.GetSharingAsync(sid);
        if (s == null)
            return Results.NotFound("sharing not found");

        // Go: s.Valid() checks
        if (s.Disabled)
            return Results.StatusCode(403);
        if (s.MaxAccessed > 0 && s.Accessed >= s.MaxAccessed)
            return Results.StatusCode(403);
        if (s.Files == null || s.Files.Count == 0)
            return Results.StatusCode(403);
        if (s.Expires.HasValue && s.Expires.Value < DateTime.UtcNow)
            return Results.StatusCode(403);

        if (!string.IsNullOrEmpty(s.Pwd))
        {
            // Go: uses query param "pwd"
            var pwd = ctx.Request.Query["pwd"].ToString();
            if (string.IsNullOrEmpty(pwd))
            {
                ctx.Request.Headers.TryGetValue("X-Sharing-Password", out var headerPwd);
                pwd = headerPwd.ToString();
            }
            if (pwd != s.Pwd)
                return Results.StatusCode(403);
        }

        // Go: cannot extract sharing root if multiple files
        var basePath = s.Files.FirstOrDefault() ?? "/";
        if (s.Files.Count != 1 && string.IsNullOrEmpty(path))
            return Results.StatusCode(403);

        var fullPath = string.IsNullOrEmpty(path) ? basePath : $"{basePath.TrimEnd('/')}/{path}";

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

        try
        {
            // Use SharpCompress to support multiple archive formats (zip, tar.gz, rar, 7z, etc.)
            await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            using var reader = SharpCompress.Readers.ReaderFactory.Open(fileStream);
            var normalizedName = innerPath.TrimStart('/');

            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                var entryKey = reader.Entry.Key ?? "";
                if (entryKey == normalizedName ||
                    entryKey.EndsWith("/" + normalizedName, StringComparison.Ordinal) ||
                    System.IO.Path.GetFileName(entryKey) == System.IO.Path.GetFileName(normalizedName))
                {
                    var ms = new MemoryStream();
                    using var entryStream = reader.OpenEntryStream();
                    await entryStream.CopyToAsync(ms);
                    ms.Position = 0;
                    return ms;
                }
            }

            return null;
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
