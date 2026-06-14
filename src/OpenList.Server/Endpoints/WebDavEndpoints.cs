using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Middleware;

namespace OpenList.Server.Endpoints;

public static class WebDavEndpoints
{
    public static IEndpointRouteBuilder MapWebDavApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/dav");

        group.MapGet("{*path}", async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = ResolvePath(ctx, path);
            if (path == null) return Results.Forbid();
            return await ServeFileAsync(fs, ctx, path);
        });

        group.MapMethods("{*path}", ["HEAD"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = ResolvePath(ctx, path);
            if (path == null) return Results.Forbid();
            return await ServeFileAsync(fs, ctx, path);
        });

        group.MapPut("{*path}", async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = ResolvePath(ctx, path);
            if (path == null) return Results.Forbid();
            var fileName = System.IO.Path.GetFileName(path);
            var parentDir = NormalizePath(System.IO.Path.GetDirectoryName(path));

            var parentObj = await fs.GetAsync(parentDir);
            if (parentObj == null || !parentObj.IsDir())
                return Results.Conflict("Parent directory not found");

            var stream = ctx.Request.Body;
            var size = ctx.Request.ContentLength ?? 0;

            var fileStreamer = new FileStreamer
            {
                Obj = new ObjItem { Name = fileName, IsFolder = false, Size = size },
                Stream = stream,
                Size = size,
                Mimetype = ctx.Request.ContentType
            };

            await fs.PutAsync(parentDir, fileStreamer, _ => { });
            return Results.Created(path, null);
        });

        group.MapDelete("{*path}", async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = ResolvePath(ctx, path);
            if (path == null) return Results.Forbid();
            await fs.RemoveAsync(path);
            return Results.NoContent();
        });

        group.MapMethods("{*path}", ["MKCOL"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = ResolvePath(ctx, path);
            if (path == null) return Results.Forbid();
            await fs.MakeDirAsync(path);
            return Results.Created(path, null);
        });

        group.MapMethods("{*path}", ["COPY"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = ResolvePath(ctx, path);
            if (path == null) return Results.Forbid();
            var destination = ctx.Request.Headers["Destination"].FirstOrDefault();
            if (string.IsNullOrEmpty(destination)) return Results.BadRequest("Destination header required");
            var dstPath = ResolvePath(ctx, destination.Replace("/dav", ""));
            if (dstPath == null) return Results.Forbid();
            await fs.CopyAsync(path, dstPath);
            return Results.Created(dstPath, null);
        });

        group.MapMethods("{*path}", ["MOVE"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = ResolvePath(ctx, path);
            if (path == null) return Results.Forbid();
            var destination = ctx.Request.Headers["Destination"].FirstOrDefault();
            if (string.IsNullOrEmpty(destination)) return Results.BadRequest("Destination header required");
            var dstPath = ResolvePath(ctx, destination.Replace("/dav", ""));
            if (dstPath == null) return Results.Forbid();
            await fs.MoveAsync(path, dstPath);
            return Results.Created(dstPath, null);
        });

        group.MapMethods("{*path}", ["PROPFIND"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = ResolvePath(ctx, path);
            if (path == null) return Results.Forbid();
            var depthStr = ctx.Request.Headers["Depth"].FirstOrDefault();
            int? depth = int.TryParse(depthStr, out var d) ? d : null;
            return await PropfindResult(fs, path, depth);
        });

        group.MapMethods("{*path}", ["PROPPATCH"], (HttpContext ctx, string? path) =>
        {
            var xml = BuildEmptyMultistatus();
            ctx.Response.StatusCode = 207;
            return Results.Text(xml, "application/xml; charset=utf-8");
        });

        group.MapMethods("{*path}", ["LOCK"], (string? path) =>
        {
            var doc = new XmlDocument();
            doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
            var prop = doc.CreateElement("D", "prop", "DAV:");
            doc.AppendChild(prop);
            prop.AppendChild(doc.CreateElement("D", "lockdiscovery", "DAV:"));
            return Results.Text(doc.OuterXml, "application/xml; charset=utf-8");
        });

        group.MapMethods("{*path}", ["UNLOCK"], () => Results.NoContent());

        endpoints.MapMethods("/dav/{*path}", ["OPTIONS"], (HttpContext ctx, string? path) =>
        {
            ctx.Response.Headers.Allow = "OPTIONS, GET, HEAD, PUT, DELETE, COPY, MOVE, MKCOL, PROPFIND, PROPPATCH, LOCK, UNLOCK";
            ctx.Response.Headers["DAV"] = "1, 2";
            return Results.Ok();
        });

        return endpoints;
    }

    /// <summary>
    /// Serves a file via WebDAV, respecting the storage's WebdavPolicy.
    /// - native_proxy: server proxies the file content
    /// - 302_redirect: redirect to the file's direct link URL
    /// - use_proxy_url: redirect to the storage's DownProxyURL
    /// </summary>
    private static async Task<IResult> ServeFileAsync(IFileSystemService fs, HttpContext ctx, string path)
    {
        var obj = await fs.GetAsync(path);
        if (obj == null) return Results.NotFound();
        if (obj.IsDir()) return await PropfindResult(fs, path, null);

        var storage = await fs.GetStorageAsync(path);
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";

        // 302_redirect: redirect to file's direct link
        if (storage?.Webdav302() == true)
        {
            var link = await fs.LinkAsync(path, new LinkArgs { IP = ip, Redirect = true });
            if (link?.URL != null)
            {
                if (link.Header != null)
                {
                    foreach (var (key, value) in link.Header)
                        ctx.Response.Headers[key] = value;
                }
                return Results.Redirect(link.URL);
            }
        }

        // use_proxy_url: redirect to DownProxyURL
        if (storage?.WebdavProxyURL() == true && !string.IsNullOrEmpty(storage.DownProxyURL))
        {
            var proxyUrl = storage.DownProxyURL.TrimEnd('/') + path;
            ctx.Response.Headers.CacheControl = "max-age=0, no-cache, no-store, must-revalidate";
            return Results.Redirect(proxyUrl);
        }

        // native_proxy: server proxies the file
        var nativeLink = await fs.LinkAsync(path, new LinkArgs { IP = ip });
        if (nativeLink == null) return Results.NotFound();

        // Local file path — serve directly with Range support
        if (System.IO.File.Exists(nativeLink.URL))
            return Results.File(nativeLink.URL, FsHelpers.GetContentType(path), obj.GetName(), enableRangeProcessing: true);

        // HTTP URL — proxy through server if ProxyRange is enabled, otherwise redirect
        if (nativeLink.URL.StartsWith("http"))
        {
            if (storage?.ProxyRange == true)
                return await ProxyHttpFileAsync(ctx, nativeLink, obj);
            return Results.Redirect(nativeLink.URL);
        }

        return Results.NotFound();
    }

    /// <summary>
    /// Proxies an HTTP file through the server, forwarding Range headers.
    /// </summary>
    private static async Task<IResult> ProxyHttpFileAsync(HttpContext ctx, Link link, IObj obj)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, link.URL);

            // Forward Range header
            var rangeHeader = ctx.Request.Headers.Range.ToString();
            if (!string.IsNullOrEmpty(rangeHeader))
                request.Headers.TryAddWithoutValidation("Range", rangeHeader);

            // Forward link headers
            if (link.Header != null)
            {
                foreach (var (key, value) in link.Header)
                    request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await http.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return Results.StatusCode((int)response.StatusCode);

            ctx.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType != null)
                ctx.Response.ContentType = response.Content.Headers.ContentType.ToString();
            if (response.Content.Headers.ContentLength.HasValue)
                ctx.Response.ContentLength = response.Content.Headers.ContentLength.Value;
            if (response.Headers.AcceptRanges.Count > 0)
                ctx.Response.Headers.AcceptRanges = "bytes";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{obj.GetName()}\"";

            await response.Content.CopyToAsync(ctx.Response.Body);
            return new EmptyResult();
        }
        catch
        {
            return Results.StatusCode(500);
        }
    }

    private sealed class EmptyResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the request path against the user's BasePath restriction.
    /// Returns null if the path is outside the user's allowed scope.
    /// </summary>
    private static string? ResolvePath(HttpContext ctx, string? rawPath)
    {
        var basePath = ctx.Items["WebDavBasePath"] as string ?? "/";
        var normalized = NormalizePath(rawPath);

        if (basePath == "/" || string.IsNullOrEmpty(basePath))
            return normalized;

        basePath = NormalizePath(basePath);
        if (!normalized.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return null; // Path outside user's allowed scope

        return normalized;
    }

    private static async Task<IResult> PropfindResult(IFileSystemService fs, string path, int? depth)
    {
        depth ??= 1;
        var obj = await fs.GetAsync(path);
        if (obj == null) return Results.NotFound();

        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
        var multistatus = doc.CreateElement("D", "multistatus", "DAV:");
        doc.AppendChild(multistatus);

        AddResponse(doc, multistatus, path, obj);

        if (depth > 0 && obj.IsDir())
        {
            var children = await fs.ListAsync(path);
            foreach (var child in children)
                AddResponse(doc, multistatus, $"{path}/{child.GetName()}".Replace("//", "/"), child);
        }

        return Results.Text(doc.OuterXml, "application/xml; charset=utf-8", statusCode: 207);
    }

    private static void AddResponse(XmlDocument xml, XmlElement parent, string path, IObj obj)
    {
        var response = xml.CreateElement("D", "response", "DAV:");
        parent.AppendChild(response);

        var href = xml.CreateElement("D", "href", "DAV:");
        href.InnerText = $"/dav{path}";
        response.AppendChild(href);

        var propStat = xml.CreateElement("D", "propstat", "DAV:");
        response.AppendChild(propStat);

        var prop = xml.CreateElement("D", "prop", "DAV:");
        propStat.AppendChild(prop);

        var resType = xml.CreateElement("D", "resourcetype", "DAV:");
        if (obj.IsDir()) resType.AppendChild(xml.CreateElement("D", "collection", "DAV:"));
        prop.AppendChild(resType);

        var displayName = xml.CreateElement("D", "displayname", "DAV:");
        displayName.InnerText = obj.GetName();
        prop.AppendChild(displayName);

        var contentLength = xml.CreateElement("D", "getcontentlength", "DAV:");
        contentLength.InnerText = obj.GetSize().ToString();
        prop.AppendChild(contentLength);

        var lastModified = xml.CreateElement("D", "getlastmodified", "DAV:");
        lastModified.InnerText = obj.GetModTime().ToString("R");
        prop.AppendChild(lastModified);

        var status = xml.CreateElement("D", "status", "DAV:");
        status.InnerText = "HTTP/1.1 200 OK";
        propStat.AppendChild(status);
    }

    private static string BuildEmptyMultistatus()
    {
        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
        doc.AppendChild(doc.CreateElement("D", "multistatus", "DAV:"));
        return doc.OuterXml;
    }

    private static string NormalizePath(string? path) => string.IsNullOrEmpty(path) ? "/" : $"/{path.Trim('/')}".Replace("//", "/");

}
