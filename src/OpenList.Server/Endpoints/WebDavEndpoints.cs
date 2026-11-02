using System.Security.Claims;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Infrastructure.Services;
using OpenList.Vfs;

namespace OpenList.Server.Endpoints;

/// <summary>
/// WebDAV 服务端点，路由位于 <c>/dav</c>。
/// 实现文件管理所需的 WebDAV（RFC 4918）子集：OPTIONS、PROPFIND、GET、HEAD、PUT、
/// MKCOL、DELETE、MOVE、COPY、LOCK、UNLOCK。
/// 将 WebDAV 根映射到 <see cref="MountFileSystem"/> 根。
/// 鉴权：既支持 HTTP Basic → OpenList 用户，也支持 JWT 透传。
/// 对应 OpenList Go <c>server/webdav</c>。
/// </summary>
public static class WebDavEndpoints
{
    private static readonly XmlSerializer MultistatusSerializer = new(typeof(DavMultistatus));

    /// <summary>
    /// 注册 <c>/dav</c> 路由组（DisableAntiforgery）。根路径 "/" 与任意 <c>/{**path}</c>
    /// 均注册上述 WebDAV 方法；认证交由 handler 内部的 <see cref="ResolveUserAsync"/>
    /// 处理（Basic 或 JWT）。
    /// </summary>
    public static IEndpointRouteBuilder MapWebDavApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/dav").WithTags("WebDAV").DisableAntiforgery();

        // 具名方法的任意路径路由（*path 捕获剩余路径）
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

        // 不带路径参数的根路径路由
        g.MapMethods("/", ["OPTIONS"], (HttpContext ctx, MountService m, IStorageService s) => OptionsAsync("", ctx, m, s));
        g.MapMethods("/", ["PROPFIND"], (HttpContext ctx, MountService m, IStorageService s, FileAccessService a, IMetaService meta) => PropfindAsync("", ctx, m, s, a, meta));
        g.MapGet("/", (HttpContext ctx, MountService m, IStorageService s, FileAccessService a) => GetAsync("", ctx, m, s, a));
        g.MapMethods("/", ["HEAD"], (HttpContext ctx, MountService m, IStorageService s, FileAccessService a) => HeadAsync("", ctx, m, s, a));
        g.MapMethods("/", ["MKCOL"], (HttpContext ctx, MountService m, IStorageService s, IFileSystemService fs, IMetaService meta) => MkcolAsync("", ctx, m, s, fs, meta));

        return app;
    }

    // --- OPTIONS：通告 DAV 能力 ---

    /// <summary>OPTIONS — 返回 DAV 能力与允许的方法列表。</summary>
    private static Task<IResult> OptionsAsync(string path, HttpContext ctx, MountService mount, IStorageService storageSvc)
    {
        ctx.Response.Headers["DAV"] = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        ctx.Response.Headers["Allow"] = "OPTIONS, GET, HEAD, PROPFIND, PUT, MKCOL, DELETE, MOVE, COPY, LOCK, UNLOCK";
        return Task.FromResult<IResult>(Results.Ok());
    }

    // --- PROPFIND：列目录或取资源属性 ---

    private static async Task<IResult> PropfindAsync(
        string path, HttpContext ctx, MountService mount, IStorageService storageSvc,
        FileAccessService access, IMetaService meta)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        // BasePath 越界 → 403，与写路径一致。
        if (DavPathGuard(user, path, out var fullPath) is { } denied) return denied;

        // 读门禁：隐藏条目 / ReadUsers / 目录密码。WebDAV 没有「目录密码」通道，
        // 因此受密码保护的 meta 目录在 WebDAV 上按无权访问处理（与 Go 的
        // CanAccess(user, meta, path, "") 一致，宁可不给也不静默放开）。
        // meta 只取一次：PROPFIND 可能枚举上千个子项，逐项查库会把一次列举变成 N 次查询。
        var metas = await access.LoadMetasAsync(ctx.RequestAborted);
        if (!access.CanSee(user, fullPath, null, metas))
            return DavDeny("password is incorrect or you have no permission");

        var upath = NormalizePath(fullPath);
        var depth = ctx.Request.Headers["Depth"].ToString();
        var searchOption = depth == "0" ? SearchDepth.TopDirectoryOnly : SearchDepth.AllDirectories;
        if (string.IsNullOrEmpty(depth)) searchOption = SearchDepth.AllDirectories; // default infinity

        var responses = new List<DavResponse>();

        // Self
        if (mount.Root.FileExists(upath))
        {
            responses.Add(ToFileResponse(mount, upath, user));
        }
        else if (mount.Root.DirectoryExists(upath))
        {
            responses.Add(ToDirResponse(mount, upath, user));
            // 子项只能逐个判定：父目录可见不代表嵌套子项可见。
            // 隐藏规则按「各自路径的父目录」匹配，因此必须逐项走同一套 CanAccess。
            var children = searchOption == SearchDepth.AllDirectories
                ? EnumerateAll(mount, upath)
                : (depth == "1"
                    ? mount.Root.EnumeratePaths(upath, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both)
                    : Enumerable.Empty<UPath>());

            foreach (var child in children)
            {
                if (!access.CanSee(user, child.FullName, null, metas)) continue;
                responses.Add(mount.Root.FileExists(child)
                    ? ToFileResponse(mount, child, user)
                    : ToDirResponse(mount, child, user));
            }
        }
        else
        {
            return Results.NotFound();
        }

        var multistatus = new DavMultistatus { Responses = responses };
        return MultistatusResult(multistatus);
    }

    // --- PROPPATCH：未完整实现，返回成功 ---

    private static async Task<IResult> ProppatchAsync(string path, HttpContext ctx, MountService mount, IStorageService storageSvc)
    {
        // PROPPATCH 在本实现里是「接受但忽略」的兼容响应，仍然要求认证 + 路径门禁：
        // 匿名请求不应拿到任何 207 响应，受限用户也不该探测自己范围外的路径。
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (DavPathGuard(user, path, out _) is { } denied) return denied;

        var response = new DavResponse
        {
            Href = "/" + path.TrimStart('/'),
            Propstat = [new DavPropstat { Status = "HTTP/1.1 200 OK" }],
        };
        return MultistatusResult(new DavMultistatus { Responses = [response] });
    }

    // --- GET：下载文件或返回目录的 HTML 列表 ---

    private static async Task<IResult> GetAsync(
        string path, HttpContext ctx, MountService mount, IStorageService storageSvc, FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        if (DavPathGuard(user, path, out var fullPath) is { } denied) return denied;
        var metas = await access.LoadMetasAsync(ctx.RequestAborted);
        if (!access.CanSee(user, fullPath, null, metas))
            return DavDeny("password is incorrect or you have no permission");

        var upath = NormalizePath(fullPath);

        // 文件：直接返回字节流
        if (mount.Root.FileExists(upath))
        {
            var entry = mount.Root.GetFileEntry(upath);
            ctx.Response.Headers["Content-Length"] = entry.Length.ToString();
            var stream = mount.Root.OpenFile(upath, FileMode.Open, FileAccess.Read);
            return Results.Stream(stream, "application/octet-stream");
        }

        // 目录：返回简单的 HTML 目录列表
        if (mount.Root.DirectoryExists(upath))
        {
            var sb = new StringBuilder();
            sb.AppendLine("<html><head><title>Index of /" + path + "</title></head><body>");
            sb.AppendLine($"<h1>Index of /{path}</h1><hr><ul>");
            if (upath != NormalizePath(MetaAccess.Normalize(string.IsNullOrEmpty(user.BasePath) ? "/" : user.BasePath)))
            {
                sb.AppendLine("<li><a href=\"../\">../</a></li>");
            }
            foreach (var child in mount.Root.EnumeratePaths(upath, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both))
            {
                // 逐项过滤：与 PROPFIND 同一套可见性判定（meta 已预加载）。
                if (!access.CanSee(user, child.FullName, null, metas)) continue;
                var name = child.GetName();
                var isDir = mount.Root.DirectoryExists(child);
                var href = DavHref(user, child);
                sb.AppendLine($"<li><a href=\"{href}\">{name}{(isDir ? "/" : "")}</a></li>");
            }
            sb.AppendLine("</ul><hr></body></html>");
            return Results.Text(sb.ToString(), "text/html", Encoding.UTF8);
        }

        return Results.NotFound();
    }

    // --- HEAD：仅返回元数据 ---

    private static async Task<IResult> HeadAsync(
        string path, HttpContext ctx, MountService mount, IStorageService storageSvc, FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        if (DavPathGuard(user, path, out var fullPath) is { } denied) return denied;
        if (!await access.CanSeeAsync(user, fullPath, null, ctx.RequestAborted))
            return DavDeny("password is incorrect or you have no permission");

        var upath = NormalizePath(fullPath);

        // 文件：填充大小等响应头返回，不写响应体
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

    // --- 写方法的权限 / 路径门禁 ---
    //
    // 这一层以前是缺的：`/dav` 的 PUT / MKCOL / DELETE / MOVE / COPY 只校验「已登录、未禁用」，
    // 于是**任何**已认证用户都能通过 WebDAV 删改文件 —— 没有 CanRemove 的账号、被 BasePath
    // 限制在某个子目录的账号，全都绕过了 `/api/fs/*` 上那套守卫。
    // Go 版没有这个洞，是因为它的 webdav 处理器直接调用 FsRemove / FsMove 等公共操作，
    // 权限检查在被调方内部；本仓库把这些操作搬到了端点层，WebDAV 就成了漏网的那条路。
    //
    // 失败一律给**真实 HTTP 状态码**：Finder / Windows 资源管理器 / rclone 只认状态码，
    // 回 200 + `{"code":403}` 会被它们当成成功。

    /// <summary>把 Resp 的业务码映射成真正的 HTTP 状态码（WebDAV 客户端不看业务码）。</summary>
    private static IResult DavFail(int code, string message) => Results.Json(
        new { code, message },
        statusCode: code switch
        {
            RespCodes.NotFound => StatusCodes.Status404NotFound,
            RespCodes.Forbidden => StatusCodes.Status403Forbidden,
            RespCodes.Unauthorized => StatusCodes.Status401Unauthorized,
            RespCodes.Conflict => StatusCodes.Status409Conflict,
            _ => code is >= 400 and < 500 ? code : StatusCodes.Status400BadRequest,
        });

    private static IResult DavDeny(string message) => DavFail(RespCodes.Forbidden, message);

    /// <summary>
    /// BasePath 越界检查：受限用户的 `/dav` 绝对路径也必须落在自己的根目录内。
    /// 与 <c>/api/fs/*</c> 用同一个判定（<see cref="User.TryJoinPath"/>），失败给 403。
    /// </summary>
    private static IResult? DavPathGuard(User user, string rawPath, out string fullPath)
    {
        var joined = "/" + rawPath.TrimStart('/');
        if (user.TryJoinPath(joined, out var path)) { fullPath = path; return null; }
        fullPath = string.Empty;
        return DavDeny("using relative path is not allowed");
    }

    /// <summary>目标是否已存在（文件或目录）。</summary>
    private static bool DavExists(MountService mount, string path)
    {
        var upath = NormalizePath(path);
        return mount.Root.FileExists(upath) || mount.Root.DirectoryExists(upath);
    }

    // --- PUT：上传文件 ---

    private static async Task<IResult> PutAsync(string path, HttpContext ctx, MountService mount, IStorageService storageSvc, IFileSystemService fs, IMetaService metaSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        if (DavPathGuard(user, path, out var fullPath) is { } pathDenied) return pathDenied;
        // 镜像 Go 的 fsup 中间件：CanWriteContent（或 meta 显式开放写入）+ 父目录 meta WriteUsers。
        if (await FsEndpoints.ContentWriteGateAsync(metaSvc, user, fullPath, ctx.RequestAborted) is not null)
            return DavDeny("permission denied");

        await using var stream = ctx.Request.Body;
        var resp = await fs.PutStreamAsync(fullPath, stream, user, ctx.RequestAborted);
        if (resp.Code != 200) return DavFail(resp.Code, resp.Message);
        return Results.Ok();
    }

    // --- MKCOL：创建目录 ---

    private static async Task<IResult> MkcolAsync(string path, HttpContext ctx, MountService mount, IStorageService storageSvc, IFileSystemService fs, IMetaService metaSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        if (DavPathGuard(user, path, out var fullPath) is { } pathDenied) return pathDenied;
        if (await FsEndpoints.ContentWriteGateAsync(metaSvc, user, fullPath, ctx.RequestAborted) is not null)
            return DavDeny("permission denied");

        var resp = await fs.MkdirAsync(fullPath, user, ctx.RequestAborted);
        if (resp.Code != 200) return DavFail(resp.Code, resp.Message);
        return Results.Ok();
    }

    // --- DELETE：删除文件或目录 ---

    private static async Task<IResult> DeleteAsync(string path, HttpContext ctx, MountService mount, IStorageService storageSvc, IFileSystemService fs, IMetaService metaSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        if (DavPathGuard(user, path, out var fullPath) is { } pathDenied) return pathDenied;
        // 镜像 Go FsRemove：CanRemove + 父目录 meta 写入校验。
        if (!user.CanRemove()) return DavDeny("permission denied");
        if (await FsEndpoints.MetaWriteGateAsync(metaSvc, user, MetaAccess.Parent(fullPath), ctx.RequestAborted) is not null)
            return DavDeny("permission denied");

        // RFC 4918 §9.6：删除不存在的资源是 404。RemoveAsync 对不存在的路径是静默成功，
        // 这里显式判一次，否则 rclone / 资源管理器会把「删了个寂寞」当成删掉了。
        if (!DavExists(mount, fullPath)) return DavFail(RespCodes.NotFound, "object not found");

        var resp = await fs.RemoveAsync([fullPath], user, ctx.RequestAborted);
        if (resp.Code != 200) return DavFail(resp.Code, resp.Message);
        return Results.NoContent();
    }

    // --- MOVE：移动/重命名 ---

    private static async Task<IResult> MoveAsync(string path, HttpContext ctx, MountService mount, IStorageService storageSvc, IFileSystemService fs, IMetaService metaSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        // 目标取自 Destination 请求头
        var destUrl = ctx.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destUrl)) return Results.BadRequest("Destination header required");

        var rawDest = ExtractPathFromUrl(destUrl, ctx);
        if (rawDest is null) return DavFail(RespCodes.BadRequest, "invalid Destination");

        // MOVE 既覆盖「移动」（跨目录，要 CanMove）也覆盖「重命名」（同目录，要 CanRename）。
        if (!user.CanMove() && !user.CanRename()) return DavDeny("permission denied");
        if (DavPathGuard(user, path, out var srcPath) is { } srcDenied) return srcDenied;
        if (DavPathGuard(user, rawDest, out var destPath) is { } destDenied) return destDenied;
        if (await FsEndpoints.MetaWriteGateAsync(metaSvc, user, MetaAccess.Parent(srcPath), ctx.RequestAborted) is not null)
            return DavDeny("permission denied");
        if (await FsEndpoints.MetaWriteGateAsync(metaSvc, user, MetaAccess.Parent(destPath), ctx.RequestAborted) is not null)
            return DavDeny("permission denied");
        if (!DavExists(mount, srcPath)) return DavFail(RespCodes.NotFound, "object not found");

        var resp = await fs.RenameAsync(srcPath, destPath, user, ctx.RequestAborted);
        if (resp.Code != 200) return DavFail(resp.Code, resp.Message);
        return Results.Ok();
    }

    // --- COPY：复制文件/目录 ---

    private static async Task<IResult> CopyAsync(string path, HttpContext ctx, MountService mount, IStorageService storageSvc, IFileSystemService fs, IMetaService metaSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        var destUrl = ctx.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destUrl)) return Results.BadRequest("Destination header required");

        var rawDest = ExtractPathFromUrl(destUrl, ctx);
        if (rawDest is null) return DavFail(RespCodes.BadRequest, "invalid Destination");

        // 镜像 Go FsCopy：CanCopy + 源的 meta 读校验 + 目标目录的 meta 写校验。
        if (!user.CanCopy()) return DavDeny("permission denied");
        if (DavPathGuard(user, path, out var srcPath) is { } srcDenied) return srcDenied;
        if (DavPathGuard(user, rawDest, out var destPath) is { } destDenied) return destDenied;
        if (await FsEndpoints.MetaReadGateAsync(metaSvc, user, MetaAccess.Parent(srcPath), ctx.RequestAborted) is not null)
            return DavDeny("permission denied");
        if (await FsEndpoints.MetaWriteGateAsync(metaSvc, user, MetaAccess.Parent(destPath), ctx.RequestAborted) is not null)
            return DavDeny("permission denied");

        // RFC 4918 §10.6：`Overwrite: F` 且目标已存在 → 412（默认允许覆盖）。
        var overwrite = !string.Equals(ctx.Request.Headers["Overwrite"].ToString(), "F", StringComparison.OrdinalIgnoreCase);
        if (!overwrite && DavExists(mount, destPath))
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);

        // 这里必须按**完整目标路径**拷：`CopyAsync` 只收目标目录、文件名沿用源名，
        // 于是「复制到同目录并改名」会被算成源=目标（Windows 上报 used by another process → 400）。
        var resp = await fs.CopyToAsync(srcPath, destPath, user, ctx.RequestAborted);
        if (resp.Code != 200) return DavFail(resp.Code, resp.Message);
        return Results.Created(destPath, null);
    }

    // --- LOCK：发放并持久化一个独占的内存锁令牌 ---
    // 对应 Go 的 webdav.NewMemLS()：按路径发放带不透明令牌的独占锁。
    // 若路径已被其他未过期令牌锁定，则返回 423 Locked。

    private static async Task<IResult> LockAsync(
        string path,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IWebDavLockStore lockStore)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        // 锁按「完整虚拟路径」记账：受限用户不能给自己的路径上锁，也不能替别人的路径上锁。
        if (DavPathGuard(user, path, out var fullPath) is { } denied) return denied;
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

    // --- UNLOCK：校验 Lock-Token 头并移除内存锁 ---

    private static async Task<IResult> UnlockAsync(
        string path,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IWebDavLockStore lockStore)
    {
        var user = await ResolveUserAsync(ctx, storageSvc);
        if (user is null) return Results.Unauthorized();
        if (user.Disabled) return Results.Forbid();

        if (DavPathGuard(user, path, out var fullPath) is { } denied) return denied;

        // 按 RFC 4918，Lock-Token 头以 <...> 包裹，先剥掉尖括号
        var rawToken = ctx.Request.Headers["Lock-Token"].ToString().Trim();
        if (rawToken.StartsWith('<') && rawToken.EndsWith('>'))
            rawToken = rawToken[1..^1];

        if (string.IsNullOrEmpty(rawToken))
            return Results.BadRequest("Lock-Token header required");

        // 只有令牌匹配才解除锁：未匹配返回 409 Conflict
        var removed = await lockStore.RemoveAsync(fullPath, rawToken, ctx.RequestAborted);
        return removed ? Results.NoContent() : Results.Conflict();
    }

    /// <summary>解析 WebDAV Timeout 请求头（"Second-3600"、"Infinite, Second-3600" 等），
    /// 结果上限为 86400 秒（24 小时），缺省 3600。</summary>
    private static int ParseTimeout(string? header)
    {
        // WebDAV Timeout 头：如 "Second-3600"、"Infinite, Second-3600" 等
        if (string.IsNullOrEmpty(header)) return 3600;
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part[7..], out var secs) && secs > 0)
                return Math.Min(secs, 86400); // 上限 24 小时
        }
        return 3600;
    }

    // --- 辅助方法 ---

    private static UPath NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return UPath.Root;
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.TrimEnd('/');
        return string.IsNullOrEmpty(path) ? UPath.Root : new UPath(path);
    }

    private static IEnumerable<UPath> EnumerateAll(MountService mount, UPath dir)
    {
        foreach (var child in mount.Root.EnumeratePaths(dir, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both))
        {
            yield return child;
            if (mount.Root.DirectoryExists(child))
            {
                foreach (var sub in EnumerateAll(mount, child))
                    yield return sub;
            }
        }
    }

    private static DavResponse ToFileResponse(MountService mount, UPath path, User user)
    {
        var entry = mount.Root.GetFileEntry(path);
        return new DavResponse
        {
            Href = DavHref(user, path),
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

    private static DavResponse ToDirResponse(MountService mount, UPath path, User user)
    {
        return new DavResponse
        {
            Href = DavHref(user, path) + (path.FullName == "/" ? "" : "/"),
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

    /// <summary>
    /// 生成客户端可见的 href。受限用户（BasePath）看到的是**以自己根目录为根**的路径 ——
    /// 直接回绝对路径会暴露 `/dav/other-user/...`，并且让客户端在错误的前缀下拼 URL。
    /// </summary>
    private static string DavHref(User user, UPath path)
    {
        var full = path.FullName;
        var basePath = MetaAccess.Normalize(string.IsNullOrEmpty(user.BasePath) ? "/" : user.BasePath);
        if (basePath != "/" && full.StartsWith(basePath, StringComparison.Ordinal))
            full = full[basePath.Length..];
        if (string.IsNullOrEmpty(full)) full = "/";
        if (!full.StartsWith('/')) full = "/" + full;
        return full == "/" ? "/dav/" : "/dav" + full;
    }

    private static string? ExtractPathFromUrl(string url, HttpContext ctx)
    {
        // Destination 可能是完整 URL（http://host/dav/path）或绝对路径（/dav/path），
        // 统一剥掉 /dav 前缀得到存储内路径
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var p = uri.AbsolutePath;
            if (p.StartsWith("/dav/", StringComparison.OrdinalIgnoreCase)) return p[4..]; // 去掉 /dav 前缀
            return p;
        }
        if (url.StartsWith("/dav/", StringComparison.OrdinalIgnoreCase)) return url[4..];
        return url;
    }

    /// <summary>
    /// 解析 WebDAV 请求的用户：优先采用 JWT 中间件已认证身份；否则解析 HTTP Basic
    /// 头、通过 IUserService 加载用户并校验密码与禁用状态。
    /// </summary>
    private static async Task<User?> ResolveUserAsync(HttpContext ctx, IStorageService storageSvc)
    {
        // 若已通过 JWT 中间件完成认证，直接复用
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            return ctx.User.ToUser();
        }

        // HTTP Basic 认证 → 映射为 OpenList 用户
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            // Basic 头为 base64(username:password)，解码后按首个 ':' 切分
            var encoded = auth[6..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var idx = decoded.IndexOf(':');
            if (idx < 0) return null;
            var username = decoded[..idx];
            var password = decoded[(idx + 1)..];

            // 加载用户并校验密码、禁用状态
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

// --- WebDAV XML 模型（RFC 4918） ---

/// <summary>multistatus 响应的根元素（含多个 response）。</summary>
[XmlRoot("multistatus", Namespace = "DAV:")]
public class DavMultistatus
{
    [XmlElement("response")]
    public List<DavResponse> Responses { get; set; } = [];
}

/// <summary>单个资源的响应（href + 属性集 propstat）。</summary>
public class DavResponse
{
    public string Href { get; set; } = "";
    [XmlElement("propstat")]
    public List<DavPropstat> Propstat { get; set; } = [];
}

/// <summary>属性集及其状态。</summary>
public class DavPropstat
{
    public DavProp Prop { get; set; } = new();
    public string Status { get; set; } = "HTTP/1.1 200 OK";
}

/// <summary>资源的属性（displayname、contentlength 等）。</summary>
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

/// <summary>资源类型（含 collection 元素表示目录）。</summary>
public class DavResourceType
{
    [XmlElement("collection")]
    public object? Collection { get; set; }
}

/// <summary>锁发现（lockdiscovery）容器。</summary>
public class DavLockDiscovery
{
    [XmlElement("activelock")]
    public DavActiveLock ActiveLock { get; set; } = new();
}

/// <summary>一个处于生效状态的锁。</summary>
public class DavActiveLock
{
    public DavLockType Type { get; set; } = new();
    public DavLockScope Scope { get; set; } = new();
    public string Depth { get; set; } = "infinity";
    public string Timeout { get; set; } = "Second-3600";
    public DavLockToken Token { get; set; } = new();
}

/// <summary>锁类型（write）。</summary>
public class DavLockType
{
    [XmlElement("write")]
    public object? Write { get; set; }
}

/// <summary>锁作用域（exclusive 独占）。</summary>
public class DavLockScope
{
    [XmlElement("exclusive")]
    public object? Exclusive { get; set; }
}

/// <summary>锁令牌（href）。</summary>
public class DavLockToken
{
    [XmlElement("href")]
    public string Href { get; set; } = "";
}
