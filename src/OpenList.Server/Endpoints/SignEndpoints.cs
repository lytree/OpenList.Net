using System.IO.Compression;
using System.Text;
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

        // /ad/*path - signed archive download (extract inner file from archive)
        endpoints.MapGet("/ad/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveDownload(fs, sign, ctx, path, sign_query)).AllowAnonymous();
        endpoints.MapMethods("/ad/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveDownload(fs, sign, ctx, path, sign_query)).AllowAnonymous();

        // /ap/*path - signed archive proxy (proxy inner file from archive)
        endpoints.MapGet("/ap/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveProxy(fs, sign, ctx, path, sign_query)).AllowAnonymous();

        // /ae/*path - signed archive extract (download extracted file)
        endpoints.MapGet("/ae/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveExtract(fs, sign, ctx, path, sign_query)).AllowAnonymous();

        // /i/:link_name - iOS Plist for streaming IPA
        endpoints.MapGet("/i/{linkName}", (string linkName) =>
        {
            var nameB64 = linkName.EndsWith(".plist") ? linkName[..^6] : linkName;
            string decoded;
            try
            {
                var bytes = Convert.FromBase64String(nameB64);
                decoded = Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return Results.BadRequest("invalid base64");
            }

            var parts = decoded.Split('/');
            if (parts.Length != 2)
                return Results.BadRequest("malformed link");

            var linkStr = Uri.UnescapeDataString(parts[0]);
            if (!Uri.TryCreate(linkStr, UriKind.Absolute, out var linkUri))
                return Results.BadRequest("invalid URL");

            var fullName = Uri.UnescapeDataString(parts[1]);
            var name = fullName;
            var identifier = $"org.opelist.{fullName}";

            var atIndex = fullName.LastIndexOf('@');
            if (atIndex >= 0)
            {
                name = fullName[..atIndex];
                identifier = fullName[(atIndex + 1)..];
            }

            var url = linkUri.ToString()
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
            name = System.Net.WebUtility.HtmlEncode(name);
            identifier = System.Net.WebUtility.HtmlEncode(identifier);

            var plist = new StringBuilder();
            plist.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?><!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">");
            plist.AppendLine("<plist version=\"1.0\">");
            plist.AppendLine("    <dict>");
            plist.AppendLine("        <key>items</key>");
            plist.AppendLine("        <array>");
            plist.AppendLine("            <dict>");
            plist.AppendLine("                <key>assets</key>");
            plist.AppendLine("                <array>");
            plist.AppendLine("                    <dict>");
            plist.AppendLine("                        <key>kind</key>");
            plist.AppendLine("                        <string>software-package</string>");
            plist.AppendLine("                        <key>url</key>");
            plist.AppendLine($"                        <string><![CDATA[{url}]]></string>");
            plist.AppendLine("                    </dict>");
            plist.AppendLine("                </array>");
            plist.AppendLine("                <key>metadata</key>");
            plist.AppendLine("                <dict>");
            plist.AppendLine("                    <key>bundle-identifier</key>");
            plist.AppendLine($"                    <string>{identifier}</string>");
            plist.AppendLine("                    <key>bundle-version</key>");
            plist.AppendLine("                    <string>4.4</string>");
            plist.AppendLine("                    <key>kind</key>");
            plist.AppendLine("                    <string>software</string>");
            plist.AppendLine("                    <key>title</key>");
            plist.AppendLine($"                    <string>{name}</string>");
            plist.AppendLine("                </dict>");
            plist.AppendLine("            </dict>");
            plist.AppendLine("        </array>");
            plist.AppendLine("    </dict>");
            plist.AppendLine("</plist>");

            return Results.Text(plist.ToString(), "application/xml", Encoding.UTF8);
        }).AllowAnonymous();

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

    private static async Task<IResult> HandleArchiveDownload(IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? signValue)
    {
        if (string.IsNullOrEmpty(signValue) || !sign.VerifyArchive(signValue, path))
            return Results.StatusCode(403);

        var innerPath = ctx.Request.Query["inner"].ToString();
        var password = ctx.Request.Query["pass"].ToString();
        var fileName = string.IsNullOrEmpty(innerPath) ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileName(innerPath);

        // Get archive link
        var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
        if (link == null) return Results.NotFound();

        // If no inner path, just download the archive itself
        if (string.IsNullOrEmpty(innerPath))
        {
            if (link.URL.StartsWith("http")) return Results.Redirect(link.URL);
            if (System.IO.File.Exists(link.URL))
                return Results.File(link.URL, GetContentType(path), System.IO.Path.GetFileName(path));
            return Results.NotFound();
        }

        // Extract inner file from archive
        var extractedStream = await ExtractArchiveFile(link.URL, innerPath, password);
        if (extractedStream == null) return Results.NotFound();

        return Results.Stream(extractedStream, GetContentType(innerPath), fileName);
    }

    private static async Task<IResult> HandleArchiveProxy(IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? signValue)
    {
        if (string.IsNullOrEmpty(signValue) || !sign.VerifyArchive(signValue, path))
            return Results.StatusCode(403);

        var innerPath = ctx.Request.Query["inner"].ToString();
        var password = ctx.Request.Query["pass"].ToString();

        var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
        if (link == null) return Results.NotFound();

        if (string.IsNullOrEmpty(innerPath))
        {
            // Proxy the archive file itself
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

        // Extract and proxy inner file
        var extractedStream = await ExtractArchiveFile(link.URL, innerPath, password);
        if (extractedStream == null) return Results.NotFound();

        return Results.Stream(extractedStream, GetContentType(innerPath), enableRangeProcessing: true);
    }

    private static async Task<IResult> HandleArchiveExtract(IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? signValue)
    {
        if (string.IsNullOrEmpty(signValue) || !sign.VerifyArchive(signValue, path))
            return Results.StatusCode(403);

        var innerPath = ctx.Request.Query["inner"].ToString();
        var password = ctx.Request.Query["pass"].ToString();

        var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
        if (link == null) return Results.NotFound();

        var extractedStream = await ExtractArchiveFile(link.URL, innerPath, password);
        if (extractedStream == null) return Results.NotFound();

        var fileName = string.IsNullOrEmpty(innerPath) ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileName(innerPath);
        return Results.Stream(extractedStream, GetContentType(innerPath), fileName);
    }

    private static async Task<Stream?> ExtractArchiveFile(string archivePath, string innerPath, string? password)
    {
        if (!System.IO.File.Exists(archivePath)) return null;

        var ext = System.IO.Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext != ".zip") return null; // Only ZIP supported for now

        try
        {
            var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(innerPath.TrimStart('/'));
            if (entry == null)
            {
                // Try finding by name only
                entry = archive.Entries.FirstOrDefault(e => e.FullName == innerPath.TrimStart('/') || e.Name == System.IO.Path.GetFileName(innerPath));
            }
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
        catch
        {
            return null;
        }
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
