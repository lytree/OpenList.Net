using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

public static class WebDavEndpoints
{
    public static IEndpointRouteBuilder MapWebDavApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/dav");

        group.MapGet("{*path}", async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = NormalizePath(path);
            var obj = await fs.GetAsync(path);
            if (obj == null) return Results.NotFound();
            if (obj.IsDir()) return await PropfindResult(fs, path, null);
            var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
            if (link == null) return Results.NotFound();
            if (link.URL.StartsWith("http")) return Results.Redirect(link.URL);
            if (System.IO.File.Exists(link.URL)) return Results.File(link.URL, GetContentType(path), obj.GetName(), enableRangeProcessing: true);
            return Results.NotFound();
        });

        group.MapMethods("{*path}", ["HEAD"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = NormalizePath(path);
            var obj = await fs.GetAsync(path);
            if (obj == null) return Results.NotFound();
            if (obj.IsDir()) return await PropfindResult(fs, path, null);
            var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
            if (link == null) return Results.NotFound();
            if (link.URL.StartsWith("http")) return Results.Redirect(link.URL);
            if (System.IO.File.Exists(link.URL)) return Results.File(link.URL, GetContentType(path), obj.GetName(), enableRangeProcessing: true);
            return Results.NotFound();
        });

        group.MapPut("{*path}", async (HttpContext ctx, string? path) =>
        {
            path = NormalizePath(path);
            using var stream = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(stream);
            return Results.Created(path, null);
        }).RequireAuthorization();

        group.MapDelete("{*path}", async (IFileSystemService fs, string? path) =>
        {
            path = NormalizePath(path);
            await fs.RemoveAsync(path);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapMethods("{*path}", ["MKCOL"], async (IFileSystemService fs, string? path) =>
        {
            path = NormalizePath(path);
            await fs.MakeDirAsync(path);
            return Results.Created(path, null);
        }).RequireAuthorization();

        group.MapMethods("{*path}", ["COPY"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = NormalizePath(path);
            var destination = ctx.Request.Headers["Destination"].FirstOrDefault();
            if (string.IsNullOrEmpty(destination)) return Results.BadRequest("Destination header required");
            var dstPath = NormalizePath(destination.Replace("/dav", ""));
            await fs.CopyAsync(path, dstPath);
            return Results.Created(dstPath, null);
        }).RequireAuthorization();

        group.MapMethods("{*path}", ["MOVE"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = NormalizePath(path);
            var destination = ctx.Request.Headers["Destination"].FirstOrDefault();
            if (string.IsNullOrEmpty(destination)) return Results.BadRequest("Destination header required");
            var dstPath = NormalizePath(destination.Replace("/dav", ""));
            await fs.MoveAsync(path, dstPath);
            return Results.Created(dstPath, null);
        }).RequireAuthorization();

        group.MapMethods("{*path}", ["PROPFIND"], async (IFileSystemService fs, HttpContext ctx, string? path) =>
        {
            path = NormalizePath(path);
            var depthStr = ctx.Request.Headers["Depth"].FirstOrDefault();
            int? depth = int.TryParse(depthStr, out var d) ? d : null;
            return await PropfindResult(fs, path, depth);
        });

        group.MapMethods("{*path}", ["PROPPATCH"], (HttpContext ctx, string? path) =>
        {
            var xml = BuildEmptyMultistatus();
            ctx.Response.StatusCode = 207;
            return Results.Text(xml, "application/xml; charset=utf-8");
        }).RequireAuthorization();

        group.MapMethods("{*path}", ["LOCK"], (string? path) =>
        {
            var doc = new XmlDocument();
            doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
            var prop = doc.CreateElement("D", "prop", "DAV:");
            doc.AppendChild(prop);
            prop.AppendChild(doc.CreateElement("D", "lockdiscovery", "DAV:"));
            return Results.Text(doc.OuterXml, "application/xml; charset=utf-8");
        }).RequireAuthorization();

        group.MapMethods("{*path}", ["UNLOCK"], () => Results.NoContent()).RequireAuthorization();

        endpoints.MapMethods("/dav/{*path}", ["OPTIONS"], (HttpContext ctx, string? path) =>
        {
            ctx.Response.Headers.Allow = "OPTIONS, GET, HEAD, PUT, DELETE, COPY, MOVE, MKCOL, PROPFIND, PROPPATCH, LOCK, UNLOCK";
            ctx.Response.Headers["DAV"] = "1, 2";
            return Results.Ok();
        });

        return endpoints;
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

    private static string GetContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4", ".mp3" => "audio/mpeg", ".pdf" => "application/pdf",
            ".txt" => "text/plain", ".html" => "text/html", ".json" => "application/json",
            _ => "application/octet-stream"
        };
    }
}
