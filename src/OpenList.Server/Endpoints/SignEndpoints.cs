using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Drivers;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.Endpoints;

public static class SignEndpoints
{
    public static IEndpointRouteBuilder MapSignRoutes(this IEndpointRouteBuilder endpoints)
    {
        // /d/*path - signed download (Go: checks ShouldProxy first)
        endpoints.MapGet("/d/{*path}", async (IFileSystemService fs, ISignService sign, IDriverFactory driverFactory, ISettingService settings, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedDownload(fs, sign, driverFactory, settings, ctx, path, sign_query, verifyArchive: false)).AllowAnonymous().WithSummary("签名下载 (重定向/代理)");
        endpoints.MapMethods("/d/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, IDriverFactory driverFactory, ISettingService settings, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedDownload(fs, sign, driverFactory, settings, ctx, path, sign_query, verifyArchive: false)).AllowAnonymous().WithSummary("签名下载 HEAD");

        // /p/*path - signed proxy
        endpoints.MapGet("/p/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedProxy(fs, sign, ctx, path, sign_query)).AllowAnonymous().WithSummary("签名代理");
        endpoints.MapMethods("/p/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleSignedProxy(fs, sign, ctx, path, sign_query)).AllowAnonymous().WithSummary("签名代理 HEAD");

        // /ad/*path - signed archive download
        endpoints.MapGet("/ad/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveDownload(fs, sign, ctx, path, sign_query)).AllowAnonymous().WithSummary("签名压缩包内文件下载");
        endpoints.MapMethods("/ad/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveDownload(fs, sign, ctx, path, sign_query)).AllowAnonymous().WithSummary("签名压缩包内文件 HEAD");

        // /ap/*path - signed archive proxy
        endpoints.MapGet("/ap/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveProxy(fs, sign, ctx, path, sign_query)).AllowAnonymous().WithSummary("签名压缩包内文件代理");
        endpoints.MapMethods("/ap/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveProxy(fs, sign, ctx, path, sign_query)).AllowAnonymous().WithSummary("签名压缩包内文件代理 HEAD");

        // /ae/*path - signed archive extract
        endpoints.MapGet("/ae/{*path}", async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveExtract(fs, sign, ctx, path, sign_query)).AllowAnonymous().WithSummary("签名压缩包提取下载");
        endpoints.MapMethods("/ae/{*path}", ["HEAD"], async (IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? sign_query) =>
            await HandleArchiveExtract(fs, sign, ctx, path, sign_query)).AllowAnonymous().WithSummary("签名压缩包提取 HEAD");

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
        }).AllowAnonymous().WithSummary("iOS Plist 流媒体安装");

        return endpoints;
    }

    private static async Task<IResult> HandleSignedDownload(IFileSystemService fs, ISignService sign, IDriverFactory driverFactory, ISettingService settings, HttpContext ctx, string path, string? signValue, bool verifyArchive)
    {
        if (string.IsNullOrEmpty(signValue) || !(verifyArchive ? sign.VerifyArchive(signValue, path) : sign.Verify(signValue, path)))
            return Results.StatusCode(403);

        // Go: check ShouldProxy before deciding redirect vs proxy
        var storage = await fs.GetStorageAsync(path);
        if (storage != null)
        {
            var driver = driverFactory.GetDriver(storage.MountPath);
            var proxyTypes = await settings.GetSettingAsync("proxy_types");
            if (ShouldProxy(driver, storage, path, proxyTypes))
                return await HandleSignedProxy(fs, sign, ctx, path, signValue);
        }

        var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "", Redirect = true });
        if (link == null) return Results.NotFound();

        if (link.URL.StartsWith("http"))
            return Results.Redirect(link.URL);

        if (System.IO.File.Exists(link.URL))
            return Results.File(link.URL, FsHelpers.GetContentType(path), enableRangeProcessing: true);

        return Results.NotFound();
    }

    private static bool ShouldProxy(IDriver? driver, Storage storage, string path, string? proxyTypes)
    {
        if (driver != null && driver.Config().MustProxy()) return true;
        if (storage.WebProxy) return true;
        if (!string.IsNullOrEmpty(proxyTypes))
        {
            var ext = Path.GetExtension(path).TrimStart('.');
            var types = proxyTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (types.Contains(ext)) return true;
        }
        return false;
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
            return Results.File(link.URL, FsHelpers.GetContentType(path), enableRangeProcessing: true);

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
            var contentType = response.Content.Headers.ContentType?.MediaType ?? FsHelpers.GetContentType(path);
            return Results.Stream(stream, contentType);
        }

        if (System.IO.File.Exists(link.URL))
            return Results.File(link.URL, FsHelpers.GetContentType(path), enableRangeProcessing: true);

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
                return Results.File(link.URL, FsHelpers.GetContentType(path), System.IO.Path.GetFileName(path));
            return Results.NotFound();
        }

        // Extract inner file from archive
        var extractedStream = await FsHelpers.ExtractArchiveFileAsync(link.URL, innerPath, password);
        if (extractedStream == null) return Results.NotFound();

        return Results.Stream(extractedStream, FsHelpers.GetContentType(innerPath), fileName);
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
                var contentType = response.Content.Headers.ContentType?.MediaType ?? FsHelpers.GetContentType(path);
                return Results.Stream(stream, contentType);
            }
            if (System.IO.File.Exists(link.URL))
                return Results.File(link.URL, FsHelpers.GetContentType(path), enableRangeProcessing: true);
            return Results.NotFound();
        }

        // Extract and proxy inner file
        var extractedStream = await FsHelpers.ExtractArchiveFileAsync(link.URL, innerPath, password);
        if (extractedStream == null) return Results.NotFound();

        return Results.Stream(extractedStream, FsHelpers.GetContentType(innerPath), enableRangeProcessing: true);
    }

    private static async Task<IResult> HandleArchiveExtract(IFileSystemService fs, ISignService sign, HttpContext ctx, string path, string? signValue)
    {
        if (string.IsNullOrEmpty(signValue) || !sign.VerifyArchive(signValue, path))
            return Results.StatusCode(403);

        var innerPath = ctx.Request.Query["inner"].ToString();
        var password = ctx.Request.Query["pass"].ToString();

        var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
        if (link == null) return Results.NotFound();

        var extractedStream = await FsHelpers.ExtractArchiveFileAsync(link.URL, innerPath, password);
        if (extractedStream == null) return Results.NotFound();

        var fileName = string.IsNullOrEmpty(innerPath) ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileName(innerPath);
        return Results.Stream(extractedStream, FsHelpers.GetContentType(innerPath), fileName);
    }
}
