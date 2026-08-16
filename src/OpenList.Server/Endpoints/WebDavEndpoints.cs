using System.Security.Claims;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using Zio;

namespace OpenList.Server.Endpoints;

/// <summary>
/// WebDAV server endpoints under <c>/dav</c>.
/// Implements the subset of WebDAV (RFC 4918) needed for file management:
/// OPTIONS, PROPFIND, GET, HEAD, PUT, MKCOL, DELETE, MOVE, COPY, LOCK, UNLOCK.
/// Maps the WebDAV root to the Zio <see cref="MountFileSystem"/> root.
/// Auth: HTTP Basic auth → OpenList user, or JWT passthrough.
/// Mirrors OpenList Go <c>server/webdav</c>.
/// </summary>
public static class WebDavEndpoints
{
    private static readonly XmlSerializer MultistatusSerializer = new(typeof(DavMultistatus));

    public static IEndpointRouteBuilder MapWebDavApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/dav").WithTags("WebDAV").DisableAntiforgery();

        g.MapMethods("/{*path}", ["OPTIONS"], OptionsAsync);
        g.MapMethods("/{*path}", ["PROPFIND"], PropfindAsync);
        g.MapMethods("/{*path}", ["PROPPATCH"], ProppatchAsync);
        g.MapGet("/{*path}", GetAsync);
        g.MapMethods("/{*path}", ["HEAD"], HeadAsync);
        g.MapPut("/{*path}", PutAsync);
        g.MapMethods("/{*path}", ["MKCOL"], MkcolAsync);
        g.MapDelete("/{*path}", DeleteAsync);
        g.MapMethods("/{*path}", ["MOVE"], MoveAsync);
        g.MapMethods("/{*path}", ["COPY"], CopyAsync);
        g.MapMethods("/{*path}", ["LOCK"], LockAsync);
        g.MapMethods("/{*path}", ["UNLOCK"], UnlockAsync);

        // Root without path parameter
        g.MapMethods("/", ["OPTIONS"], (HttpContext ctx, ZioMountService m, IStorageService s) => OptionsAsync("", ctx, m, s));
        g.MapMethods("/", ["PROPFIND"], (HttpContext ctx, ZioMountService m, IStorageService s) => PropfindAsync("", ctx, m, s));
        g.MapGet("/", (HttpContext ctx, ZioMountService m, IStorageService s) => GetAsync("", ctx, m, s));
        g.MapMethods("/", ["HEAD"], (HttpContext ctx, ZioMountService m, IStorageService s) => HeadAsync("", ctx, m, s));
        g.MapMethods("/", ["MKCOL"], (HttpContext ctx, ZioMountService m, IStorageService s, IFileSystemService fs) => MkcolAsync("", ctx, m, s, fs));

        return app;
    }

    // --- OPTIONS: report DAV capabilities ---

    private static Task<IResult> OptionsAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc)
    {
        ctx.Response.Headers["DAV"] = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        ctx.Response.Headers["Allow"] = "OPTIONS, GET, HEAD, PROPFIND, PUT, MKCOL, DELETE, MOVE, COPY, LOCK, UNLOCK";
        return Task.FromResult<IResult>(Results.Ok());
    }

    // --- PROPFIND: list directory or get resource properties ---

    private static async Task<IResult> PropfindAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();

        var upath = NormalizePath(path);
        var depth = ctx.Request.Headers["Depth"].ToString();
        var searchOption = depth == "0" ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
        if (string.IsNullOrEmpty(depth)) searchOption = SearchOption.AllDirectories; // default infinity

        var responses = new List<DavResponse>();

        // Self
        if (mount.Root.FileExists(upath))
        {
            responses.Add(ToFileResponse(mount, upath));
        }
        else if (mount.Root.DirectoryExists(upath))
        {
            responses.Add(ToDirResponse(mount, upath));
            if (searchOption == SearchOption.AllDirectories)
            {
                foreach (var child in EnumerateAll(mount, upath))
                {
                    responses.Add(mount.Root.FileExists(child)
                        ? ToFileResponse(mount, child)
                        : ToDirResponse(mount, child));
                }
            }
            else if (searchOption == SearchOption.TopDirectoryOnly && depth == "1")
            {
                foreach (var child in mount.Root.EnumeratePaths(upath, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both))
                {
                    responses.Add(mount.Root.FileExists(child)
                        ? ToFileResponse(mount, child)
                        : ToDirResponse(mount, child));
                }
            }
        }
        else
        {
            return Results.NotFound();
        }

        var multistatus = new DavMultistatus { Responses = responses };
        return MultistatusResult(multistatus);
    }

    // --- PROPPATCH: not fully implemented, return success ---

    private static Task<IResult> ProppatchAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc)
    {
        var upath = NormalizePath(path);
        var response = new DavResponse
        {
            Href = "/" + path.TrimStart('/'),
            Propstat = [new DavPropstat { Status = "HTTP/1.1 200 OK" }],
        };
        return Task.FromResult(MultistatusResult(new DavMultistatus { Responses = [response] }));
    }

    // --- GET: download file or list directory (HTML) ---

    private static async Task<IResult> GetAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();

        var upath = NormalizePath(path);

        if (mount.Root.FileExists(upath))
        {
            var entry = mount.Root.GetFileEntry(upath);
            ctx.Response.Headers["Content-Length"] = entry.Length.ToString();
            var stream = mount.Root.OpenFile(upath, FileMode.Open, FileAccess.Read);
            return Results.Stream(stream, "application/octet-stream");
        }

        if (mount.Root.DirectoryExists(upath))
        {
            // Return a simple HTML directory listing
            var sb = new StringBuilder();
            sb.AppendLine("<html><head><title>Index of /" + path + "</title></head><body>");
            sb.AppendLine($"<h1>Index of /{path}</h1><hr><ul>");
            if (upath != UPath.Root)
            {
                sb.AppendLine("<li><a href=\"../\">../</a></li>");
            }
            foreach (var child in mount.Root.EnumeratePaths(upath, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both))
            {
                var name = child.GetName();
                var isDir = mount.Root.DirectoryExists(child);
                var href = "/" + child.FullName.TrimStart('/') + (isDir ? "/" : "");
                sb.AppendLine($"<li><a href=\"{href}\">{name}{(isDir ? "/" : "")}</a></li>");
            }
            sb.AppendLine("</ul><hr></body></html>");
            return Results.Text(sb.ToString(), "text/html", Encoding.UTF8);
        }

        return Results.NotFound();
    }

    // --- HEAD: metadata only ---

    private static async Task<IResult> HeadAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();

        var upath = NormalizePath(path);

        if (mount.Root.FileExists(upath))
        {
            var entry = mount.Root.GetFileEntry(upath);
            ctx.Response.Headers["Content-Length"] = entry.Length.ToString();
            ctx.Response.Headers["Content-Type"] = "application/octet-stream";
            ctx.Response.Headers["Last-Modified"] = entry.LastWriteTime.ToUniversalTime().ToString("R");
            return Results.Ok();
        }
        if (mount.Root.DirectoryExists(upath))
        {
            return Results.Ok();
        }
        return Results.NotFound();
    }

    // --- PUT: upload file ---

    private static async Task<IResult> PutAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc, IFileSystemService fs)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        var fullPath = "/" + path.TrimStart('/');
        await using var stream = ctx.Request.Body;
        var resp = await fs.PutStreamAsync(fullPath, stream, user, ctx.RequestAborted);
        if (resp.Code != 200) return Results.BadRequest(resp.Message);
        return Results.Ok();
    }

    // --- MKCOL: create directory ---

    private static async Task<IResult> MkcolAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc, IFileSystemService fs)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        var fullPath = "/" + path.TrimStart('/');
        var resp = await fs.MkdirAsync(fullPath, user, ctx.RequestAborted);
        if (resp.Code != 200) return Results.BadRequest(resp.Message);
        return Results.Ok();
    }

    // --- DELETE: delete file or directory ---

    private static async Task<IResult> DeleteAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc, IFileSystemService fs)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        var fullPath = "/" + path.TrimStart('/');
        var resp = await fs.RemoveAsync([fullPath], user, ctx.RequestAborted);
        if (resp.Code != 200) return Results.BadRequest(resp.Message);
        return Results.NoContent();
    }

    // --- MOVE: move/rename ---

    private static async Task<IResult> MoveAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc, IFileSystemService fs)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        var destUrl = ctx.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destUrl)) return Results.BadRequest("Destination header required");

        var destPath = ExtractPathFromUrl(destUrl, ctx);
        if (destPath is null) return Results.BadRequest("invalid Destination");

        var srcPath = "/" + path.TrimStart('/');
        var resp = await fs.RenameAsync(srcPath, destPath, user, ctx.RequestAborted);
        if (resp.Code != 200) return Results.BadRequest(resp.Message);
        return Results.Ok();
    }

    // --- COPY: copy file/directory ---

    private static async Task<IResult> CopyAsync(string path, HttpContext ctx, ZioMountService mount, IStorageService storageSvc, IFileSystemService fs)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        var destUrl = ctx.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destUrl)) return Results.BadRequest("Destination header required");

        var destPath = ExtractPathFromUrl(destUrl, ctx);
        if (destPath is null) return Results.BadRequest("invalid Destination");

        var srcPath = "/" + path.TrimStart('/');
        var destDir = destPath[..destPath.LastIndexOf('/')];
        var resp = await fs.CopyAsync([srcPath], destDir, user, ctx.RequestAborted);
        if (resp.Code != 200) return Results.BadRequest(resp.Message);
        return Results.Ok();
    }

    // --- LOCK: issue and persist an exclusive in-memory lock token ---
    // Mirrors Go's webdav.NewMemLS(): per-path exclusive locks with opaque tokens.
    // If the path is already locked by another (non-expired) token, return 423 Locked.

    private static async Task<IResult> LockAsync(
        string path,
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IWebDavLockStore lockStore)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();

        var fullPath = "/" + path.TrimStart('/');
        var depth = ctx.Request.Headers["Depth"].ToString();
        if (string.IsNullOrEmpty(depth)) depth = "infinity";

        var timeoutSeconds = ParseTimeout(ctx.Request.Headers["Timeout"].ToString());

        // If already locked (and not expired), refuse with 423 Locked.
        var existing = await lockStore.GetAsync(fullPath, ctx.RequestAborted);
        if (existing is not null)
        {
            return Results.StatusCode(StatusCodes.Status423Locked);
        }

        var token = "opaquelocktoken:" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await lockStore.SetAsync(fullPath, new WebDavLock(token, "exclusive", depth, timeoutSeconds, now), ctx.RequestAborted);

        ctx.Response.Headers["Lock-Token"] = $"<{token}>";

        var activelock = new DavActiveLock
        {
            Type = new DavLockType(),
            Scope = new DavLockScope { Exclusive = new object() },
            Depth = depth,
            Timeout = $"Second-{timeoutSeconds}",
            Token = new DavLockToken { Href = token },
        };
        var prop = new DavProp { LockDiscovery = new DavLockDiscovery { ActiveLock = activelock } };
        var response = new DavResponse
        {
            Href = fullPath,
            Propstat = [new DavPropstat { Prop = prop, Status = "HTTP/1.1 200 OK" }],
        };
        return MultistatusResult(new DavMultistatus { Responses = [response] });
    }

    // --- UNLOCK: validate the Lock-Token header and remove the in-memory lock ---

    private static async Task<IResult> UnlockAsync(
        string path,
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IWebDavLockStore lockStore)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();

        var fullPath = "/" + path.TrimStart('/');

        // Lock-Token header is wrapped in <...> per RFC 4918.
        var rawToken = ctx.Request.Headers["Lock-Token"].ToString().Trim();
        if (rawToken.StartsWith('<') && rawToken.EndsWith('>'))
            rawToken = rawToken[1..^1];

        if (string.IsNullOrEmpty(rawToken))
            return Results.BadRequest("Lock-Token header required");

        var removed = await lockStore.RemoveAsync(fullPath, rawToken, ctx.RequestAborted);
        return removed ? Results.NoContent() : Results.Conflict();
    }

    private static int ParseTimeout(string? header)
    {
        // WebDAV Timeout header: "Second-3600", "Infinite, Second-3600", etc.
        if (string.IsNullOrEmpty(header)) return 3600;
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part[7..], out var secs) && secs > 0)
                return Math.Min(secs, 86400); // cap at 24h
        }
        return 3600;
    }

    // --- Helpers ---

    private static UPath NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return UPath.Root;
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.TrimEnd('/');
        return string.IsNullOrEmpty(path) ? UPath.Root : new UPath(path);
    }

    private static IEnumerable<UPath> EnumerateAll(ZioMountService mount, UPath dir)
    {
        foreach (var child in mount.Root.EnumeratePaths(dir, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both))
        {
            yield return child;
            if (mount.Root.DirectoryExists(child))
            {
                foreach (var sub in EnumerateAll(mount, child))
                    yield return sub;
            }
        }
    }

    private static DavResponse ToFileResponse(ZioMountService mount, UPath path)
    {
        var entry = mount.Root.GetFileEntry(path);
        return new DavResponse
        {
            Href = "/dav/" + path.FullName.TrimStart('/'),
            Propstat = [new DavPropstat
            {
                Prop = new DavProp
                {
                    DisplayName = entry.Name,
                    ContentLength = entry.Length.ToString(),
                    GetContentType = "application/octet-stream",
                    GetLastModified = entry.LastWriteTime.ToUniversalTime().ToString("R"),
                    GetETag = $"\"{entry.Length:x}\"",
                    ResourceType = new DavResourceType(),
                },
                Status = "HTTP/1.1 200 OK",
            }],
        };
    }

    private static DavResponse ToDirResponse(ZioMountService mount, UPath path)
    {
        return new DavResponse
        {
            Href = "/dav/" + path.FullName.TrimStart('/') + "/",
            Propstat = [new DavPropstat
            {
                Prop = new DavProp
                {
                    DisplayName = path == UPath.Root ? "root" : path.GetName(),
                    ContentLength = "0",
                    GetContentType = "httpd/unix-directory",
                    GetLastModified = mount.Root.GetDirectoryEntry(path).LastWriteTime.ToUniversalTime().ToString("R"),
                    ResourceType = new DavResourceType { Collection = new object() },
                },
                Status = "HTTP/1.1 200 OK",
            }],
        };
    }

    private static string? ExtractPathFromUrl(string url, HttpContext ctx)
    {
        // Destination may be a full URL (http://host/dav/path) or absolute path (/dav/path)
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var p = uri.AbsolutePath;
            if (p.StartsWith("/dav/", StringComparison.OrdinalIgnoreCase)) return p[4..]; // strip /dav prefix
            return p;
        }
        if (url.StartsWith("/dav/", StringComparison.OrdinalIgnoreCase)) return url[4..];
        return url;
    }

    private static async Task<User?> ResolveUserAsync(HttpContext ctx, IStorageService storageSvc)
    {
        // If authenticated via JWT middleware, use that.
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            return ctx.User.ToUser();
        }

        // HTTP Basic auth → OpenList user
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var encoded = auth[6..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var idx = decoded.IndexOf(':');
            if (idx < 0) return null;
            var username = decoded[..idx];
            var password = decoded[(idx + 1)..];

            // Look up user and verify password
            using var scope = ctx.RequestServices.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var resp = await userService.GetByNameAsync(username, ctx.RequestAborted);
            if (resp.Code != 200 || resp.Data is null) return null;
            if (!UserService.VerifyPassword(password, resp.Data.PasswordHash)) return null;
            if (resp.Data.Disabled) return null;
            return resp.Data;
        }
        catch
        {
            return null;
        }
    }

    private static IResult MultistatusResult(DavMultistatus multistatus)
    {
        using var sw = new StringWriter();
        using var xw = XmlWriter.Create(sw, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false });
        MultistatusSerializer.Serialize(xw, multistatus);
        return Results.Text(sw.ToString(), "application/xml; charset=utf-8", Encoding.UTF8, statusCode: 207);
    }
}

// --- WebDAV XML models (RFC 4918) ---

[XmlRoot("multistatus", Namespace = "DAV:")]
public class DavMultistatus
{
    [XmlElement("response")]
    public List<DavResponse> Responses { get; set; } = [];
}

public class DavResponse
{
    public string Href { get; set; } = "";
    [XmlElement("propstat")]
    public List<DavPropstat> Propstat { get; set; } = [];
}

public class DavPropstat
{
    public DavProp Prop { get; set; } = new();
    public string Status { get; set; } = "HTTP/1.1 200 OK";
}

public class DavProp
{
    [XmlElement("displayname")]
    public string? DisplayName { get; set; }
    [XmlElement("getcontentlength")]
    public string? ContentLength { get; set; }
    [XmlElement("getcontenttype")]
    public string? GetContentType { get; set; }
    [XmlElement("getlastmodified")]
    public string? GetLastModified { get; set; }
    [XmlElement("getetag")]
    public string? GetETag { get; set; }
    [XmlElement("resourcetype")]
    public DavResourceType ResourceType { get; set; } = new();
    [XmlElement("lockdiscovery")]
    public DavLockDiscovery? LockDiscovery { get; set; }
}

public class DavResourceType
{
    [XmlElement("collection")]
    public object? Collection { get; set; }
}

public class DavLockDiscovery
{
    [XmlElement("activelock")]
    public DavActiveLock ActiveLock { get; set; } = new();
}

public class DavActiveLock
{
    public DavLockType Type { get; set; } = new();
    public DavLockScope Scope { get; set; } = new();
    public string Depth { get; set; } = "infinity";
    public string Timeout { get; set; } = "Second-3600";
    public DavLockToken Token { get; set; } = new();
}

public class DavLockType
{
    [XmlElement("write")]
    public object? Write { get; set; }
}

public class DavLockScope
{
    [XmlElement("exclusive")]
    public object? Exclusive { get; set; }
}

public class DavLockToken
{
    [XmlElement("href")]
    public string Href { get; set; } = "";
}
