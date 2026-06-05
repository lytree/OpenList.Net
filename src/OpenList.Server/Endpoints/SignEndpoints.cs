using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

public static class SignEndpoints
{
    public static IEndpointRouteBuilder MapSignRoutes(this IEndpointRouteBuilder endpoints)
    {
        // /d/*path - signed download
        endpoints.MapGet("/d/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedRequest(fs, sign, ctx, path, sign_query, verifyArchive: false)).AllowAnonymous();
        endpoints.MapMethods("/d/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedRequest(fs, sign, ctx, path, sign_query, verifyArchive: false)).AllowAnonymous();

        // /p/*path - signed proxy
        endpoints.MapGet("/p/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedProxy(fs, sign, ctx, path, sign_query)).AllowAnonymous();
        endpoints.MapMethods("/p/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedProxy(fs, sign, ctx, path, sign_query)).AllowAnonymous();

        // /ad/*path - signed archive download
        endpoints.MapGet("/ad/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedRequest(fs, sign, ctx, path, sign_query, verifyArchive: true)).AllowAnonymous();
        endpoints.MapMethods("/ad/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedRequest(fs, sign, ctx, path, sign_query, verifyArchive: true)).AllowAnonymous();

        // /ap/*path - signed archive proxy
        endpoints.MapGet("/ap/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            Results.Json(new { code = 501, message = "not implemented" })).AllowAnonymous();

        // /ae/*path - signed archive extract
        endpoints.MapGet("/ae/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            Results.Json(new { code = 501, message = "not implemented" })).AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> HandleSignedRequest(IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? signValue, bool verifyArchive)
    {
        if (string.IsNullOrEmpty(signValue) || !(verifyArchive ? sign.VerifyArchive(signValue, path) : sign.Verify(signValue, path)))
            return Results.StatusCode(403);

        var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
        if (link == null) return Results.NotFound();

        if (link.URL.StartsWith("http"))
            return Results.Redirect(link.URL);

        if (System.IO.File.Exists(link.URL))
            return Results.File(link.URL, GetContentType(path), enableRangeProcessing: true);

        return Results.NotFound();
    }

    private static async Task<IResult> HandleSignedProxy(IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? signValue)
    {
        if (string.IsNullOrEmpty(signValue) || !sign.Verify(signValue, path))
            return Results.StatusCode(403);

        var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
        if (link == null) return Results.NotFound();

        if (link.URL.StartsWith("http"))
        {
            using var httpClient = new HttpClient();
            foreach (var header in link.Header ?? new())
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

            var response = await httpClient.GetAsync(link.URL, HttpCompletionOption.ResponseHeadersRead);
            var stream = await response.Content.ReadAsStreamAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? GetContentType(path);
            return Results.Stream(stream, contentType);
        }

        if (System.IO.File.Exists(link.URL))
            return Results.File(link.URL, GetContentType(path), enableRangeProcessing: true);

        return Results.NotFound();
    }

    private static string GetContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4", ".mp3" => "audio/mpeg", ".pdf" => "application/pdf",
            ".zip" => "application/zip", ".tar" => "application/x-tar",
            ".gz" or ".tgz" => "application/gzip", ".rar" => "application/vnd.rar",
            ".7z" => "application/x-7z-compressed", ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp",
            ".txt" => "text/plain", ".html" => "text/html", ".json" => "application/json",
            ".xml" => "application/xml", _ => "application/octet-stream"
        };
    }
}
