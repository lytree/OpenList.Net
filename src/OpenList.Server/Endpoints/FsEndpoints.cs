using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Server.Middleware;
using OpenList.Server.Models;
using OpenList.Vfs;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 文件系统端点：list、get、mkdir、rename、move、copy、remove、upload、dirs、search 等。
/// 对应 OpenList Go <c>server/handles/fsread.go</c> + <c>fsmanage.go</c>。
/// 写端点需登录鉴权；只读端点（list/get/dirs/search/archive/list/other）匿名可访问
/// （受访客开关与目录元数据约束），/fs/link 仅管理员。
/// </summary>
public static class FsEndpoints
{
    /// <summary>
    /// 注册 <c>/api/fs</c> 文件系统路由组。整体 <c>RequireAuthorization</c>，
    /// 个别只读端点使用 <c>AllowAnonymous</c>，/link 升级为 <c>admin</c>。
    /// </summary>
    public static IEndpointRouteBuilder MapFsApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/fs").WithTags("Fs").RequireAuthorization();

        // 首页（文件浏览）的访客开关：**默认要求登录**（设置项 home_guest_access，默认 false）。
        // 匿名请求只有在管理员显式打开 home_guest_access 时才放行（只读浏览）。
        //
        // 用组级 endpoint filter 而不是去动各端点的 AllowAnonymous：后者是启动期常量。
        // 授权中间件在前，所以写端点（无 AllowAnonymous）根本走不到这里。
        g.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.User.Identity?.IsAuthenticated == true) return await next(context);

            var access = http.RequestServices.GetService<IGuestAccessService>();
            if (access is null || await access.CanBrowseHomeAsync(http.RequestAborted))
                return await next(context);

            return Results.Json(
                Resp.Fail("login is required to browse files", RespCodes.Unauthorized),
                statusCode: StatusCodes.Status401Unauthorized);
        });

        // --- 读取 ---
        // Go's fsAndShare group allows guest access (Auth(true)) for these read endpoints.
        // AllowAnonymous here lets unauthenticated visitors browse public shares; JWT, if
        // present, is still parsed by JwtBearer so ClaimsPrincipal.ToUser() yields the real
        // user (or a guest User with Id=0 when anonymous).
        // Go registers these via g.Any(...) — accept GET or POST for third-party
        // clients that follow the Go convention (the frontend itself uses POST).
        // POST binds the JSON body; GET binds the DTO from query params.
        g.MapMethods("/list", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<FsListReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            req.Path = Clean(req.Path);
            // 镜像 Go FsList：CanAccess(user, meta, path, req.Password)。
            if (await AccessGateAsync(metaSvc, u, req.Path, req.Password, ct) is { } denied) return denied;
            return Results.Ok(await svc.ListAsync(req, u));
        }).AllowAnonymous();

        g.MapMethods("/get", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<FsGetReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            req.Path = Clean(req.Path);
            // 镜像 Go FsGet：CanAccess(user, meta, path, req.Password)。
            // 这一道同时是直链的保护来源：未通过密码校验就拿不到 sign/raw_url，
            // 而密码目录的 /d/ 由 needSign 强制要求签名。
            if (await AccessGateAsync(metaSvc, u, req.Path, req.Password, ct) is { } denied) return denied;
            return Results.Ok(await svc.GetAsync(req, u));
        }).AllowAnonymous();

        g.MapMethods("/dirs", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<DirsReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            // Go FsDirs：force_root 仅管理员可用（列出全部挂载根）；否则按请求路径。
            if (req.ForceRoot && !u.IsAdmin())
                return Results.Ok(Resp.Fail("permission denied", RespCodes.Forbidden));
            var path = Clean(req.Path);
            // 镜像 Go FsDirs：CanAccess(user, meta, path, req.Password)（此前 password 被忽略）。
            if (await AccessGateAsync(metaSvc, u, path, req.Password, ct) is { } denied) return denied;
            return Results.Ok(await svc.DirsAsync(path, u));
        }).AllowAnonymous();

        g.MapMethods("/search", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<SearchReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            req.Parent = Clean(req.Parent);
            return Results.Ok(await svc.SearchAsync(req, u));
        }).AllowAnonymous();

        // --- Manage ---
        g.MapPost("/mkdir", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.MkdirAsync(Clean(req.Path), user.ToUser())));

        // POST /fs/rename — body: { path, name, overwrite }. newPath = parent(path) + name.
        // 新名字必须是纯名称（镜像 Go FsRename 的 checkRelativePath(req.Name)）。
        g.MapPost("/rename", async (RenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var u = user.ToUser();
            var reqPath = Clean(req.Path);
            if (!UPath.IsValidName(req.Name))
                return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.Forbidden));
            var parent = ParentPath(reqPath);
            var newPath = parent == "/" ? $"/{req.Name}" : $"{parent}/{req.Name}";
            return Results.Ok(await svc.RenameAsync(reqPath, newPath, u));
        });

        // 旧版 API 兼容入口：Items 携带完整路径（非本项目的 Go 对齐面）。
        g.MapPost("/rename_batch", async (BatchRenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var items = req.Items
                .Select(it => new BatchRenameItem { SrcName = Clean(it.SrcName), NewName = Clean(it.NewName) })
                .ToList();
            return Results.Ok(await svc.BatchRenameAsync(items, user.ToUser()));
        });

        // POST /fs/batch_rename — frontend variant. Body: { src_dir, rename_objects: [{src_name, new_name}] }
        // rename_objects carry only file names; src_dir is prepended to form full paths.
        // 名称必须是纯名称（镜像 Go FsBatchRename 的 checkRelativePath）。
        g.MapPost("/batch_rename", async (FsBatchRenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var dir = Clean(req.SrcDir);
            var baseDir = string.IsNullOrEmpty(dir) || dir == "/" ? "" : dir.TrimEnd('/');
            var items = new List<BatchRenameItem>(req.RenameObjects.Count);
            foreach (var o in req.RenameObjects)
            {
                if (string.IsNullOrEmpty(o.SrcName) || string.IsNullOrEmpty(o.NewName)) continue;
                if (!UPath.IsValidName(o.SrcName) || !UPath.IsValidName(o.NewName))
                    return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.Forbidden));
                items.Add(new BatchRenameItem
                {
                    SrcName = baseDir == "" ? $"/{o.SrcName}" : $"{baseDir}/{o.SrcName}",
                    NewName = baseDir == "" ? $"/{o.NewName}" : $"{baseDir}/{o.NewName}",
                });
            }
            return Results.Ok(await svc.BatchRenameAsync(items, user.ToUser()));
        });

        // POST /fs/regex_rename — Go API completeness (the frontend computes new
        // names client-side and uses /batch_rename instead).
        g.MapPost("/regex_rename", async (RegexRenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RegexRenameAsync(
                Clean(req.SrcDir), req.SrcNameRegex, req.NewNameRegex, user.ToUser())));

        // POST /fs/move — taskified, mirrors Go FsMove.
        // Body: { src_dir, dst_dir, names, overwrite, skip_existing }.
        g.MapPost("/move", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var mc = ToMoveCopy(req);
            mc.SrcDir = Clean(req.SrcDir);
            mc.DstDir = Clean(req.DstDir);
            return Results.Ok(await svc.MoveBatchAsync(mc, user.ToUser()));
        });

        // POST /fs/copy — taskified with optional merge, mirrors Go FsCopy.
        g.MapPost("/copy", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var mc = ToMoveCopy(req);
            mc.SrcDir = Clean(req.SrcDir);
            mc.DstDir = Clean(req.DstDir);
            return Results.Ok(await svc.CopyBatchAsync(mc, user.ToUser()));
        });

        // POST /fs/remove — body: { dir, names }. paths = join(dir)/names.
        // names 必须是纯名称：含路径成分（/ \ . ..）时返回 403（镜像 Go checkRelativePath）。
        // 否则 UPath 归一化会解析 "\..\.."，拼接后可越出 dir 删除任意文件。
        g.MapPost("/remove", async (RemoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            if (req.Names.Any(n => !UPath.IsValidName(n)))
                return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.Forbidden));
            var dir = Clean(req.Dir);
            return Results.Ok(await svc.RemoveAsync(JoinPaths(dir, req.Names), user.ToUser()));
        });

        g.MapPost("/remove_empty_directory", async (RemoveEmptyDirectoryReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            // Go 的请求字段叫 `src_dir`，本仓库早期用的是 `path`；两者都接受，
            // 否则按上游契约调用的客户端会拿到默认值 `/` —— 那会去扫整个站点根目录。
            var path = Clean(string.IsNullOrEmpty(req.Path) ? req.SrcDir : req.Path);
            return Results.Ok(await svc.RemoveEmptyDirectoryAsync(path, user.ToUser()));
        });

        // --- Upload ---
        // Go: handles.FsStream + middlewares.FsUp. Path from "File-Path" header (URL-encoded).
        g.MapPut("/put", async (
            HttpContext ctx, IFileSystemService svc,
            ClaimsPrincipal user, RateLimitManager limits) =>
        {
            var path = ReadFilePathHeader(ctx);
            if (path is null)
                return Results.Ok(Resp.Fail("File-Path header is required", RespCodes.BadRequest));
            var joined = Clean(path);
            // 客户端上传限速：按从请求体实际读出的字节扣令牌（不限速时是同一个流）。
            await limits.EnsureFreshAsync(ctx.RequestAborted);
            await using var stream = TrafficLimit.WrapUpload(ctx.Request.Body, limits.ClientUpload);
            return Results.Ok(await svc.PutStreamAsync(joined, stream, user.ToUser(), ctx.RequestAborted));
        });

        // Go: handles.FsForm + middlewares.FsUp. Multipart form upload. Path from "File-Path" header.
        g.MapPut("/form", async (
            HttpContext ctx, IFileSystemService svc,
            ClaimsPrincipal user, RateLimitManager limits) =>
        {
            var path = ReadFilePathHeader(ctx);
            if (path is null)
                return Results.Ok(Resp.Fail("File-Path header is required", RespCodes.BadRequest));
            var joined = Clean(path);
            var u = user.ToUser();
            // 限速必须包在 ReadFormAsync 之前：表单解析直接从 Request.Body 读原始字节，
            // 事后再包文件流只能限制磁盘读取，对入站带宽毫无作用。
            await limits.EnsureFreshAsync(ctx.RequestAborted);
            ctx.Request.Body = TrafficLimit.WrapUpload(ctx.Request.Body, limits.ClientUpload);
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                var r = await svc.PutFormAsync(joined, stream, file.FileName, u, ctx.RequestAborted);
                if (r.Code != 200) return Results.Ok(r);
            }
            return Results.Ok(Resp.Ok());
        });

        // Go: handles.FsGetDirectUploadInfo. Returns presigned upload URL if driver supports it.
        g.MapPost("/get_direct_upload_info", async (DirectUploadInfoReq req, IFileSystemService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var path = Clean(req.Path);
            // file_name 必须是纯名称（镜像 Go FsGetDirectUploadInfo 的 checkRelativePath(req.FileName)）。
            if (!UPath.IsValidName(req.FileName))
                return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.Forbidden));
            return Results.Ok(await svc.GetDirectUploadInfoAsync(path, req.FileSize, user.ToUser(), ct));
        });

        // Go: handles.FsRecursiveMove (alias for move with single src).
        g.MapPost("/recursive_move", async (RecursiveMoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RecursiveMoveAsync(Clean(req.SrcDir), Clean(req.DstDir), user.ToUser())));

        // --- Link / Archive ---
        // Go: g.POST("/link", middlewares.AuthAdmin, handles.Link) — link generation
        // is admin-only because it can expose storage credentials/direct URLs.
        g.MapPost("/link", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.LinkAsync(req.Path, user.ToUser()))).RequireAuthorization("admin");

        g.MapMethods("/archive/meta", ["GET", "POST"], async (IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ArchiveMetaAsync("/", user.ToUser())));

        // 归档条目列表：与 /list、/get 同级，是**只读浏览**能力，匿名访客也要能用
        // （否则公开目录里点开压缩包会 401 把人踢去登录页）。
        // 访问控制交给 meta 门禁：目录密码/隐藏条目与 /list 完全一致。
        // 注意区分两个密码：`password` 是目录密码（走门禁），`archive_pass` 才是压缩包密码。
        g.MapMethods("/archive/list", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<ArchiveListReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            var path = Clean(req.Path);
            if (await AccessGateAsync(metaSvc, u, path, req.Password, ct) is { } denied) return denied;
            return Results.Ok(await svc.ArchiveListAsync(path, req.ArchivePass, u));
        }).AllowAnonymous();

        // POST /fs/archive/decompress — body: { src_dir, dst_dir, name, archive_pass, ... }.
        // archivePath = join(src_dir + name[0]); destPath = join(dst_dir) (+ name folder if put_into_new_dir).
        g.MapPost("/archive/decompress", async (ArchiveDecompressReq req, IFileSystemService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            var archiveRel = JoinPaths(Clean(req.SrcDir), req.Name).FirstOrDefault() ?? Clean(req.SrcDir);
            var archivePath = Clean(archiveRel);
            var destDir = Clean(req.DstDir);
            if (req.PutIntoNewDir && req.Name.Count > 0)
            {
                // 对齐 Go：新目录名要**去掉扩展名**（bundle.zip → bundle/）。
                // 用完整文件名会把目标目录建成 `bundle.zip`，与归档文件本身同名，
                // 驱动的 CreateDirectory 必然冲突（表现为 500 "already exists"）。
                var fileName = req.Name[0].TrimEnd('/').Split('/').Last();
                var folder = Path.GetFileNameWithoutExtension(fileName);
                destDir = destDir.TrimEnd('/') + "/" + folder;
            }
            return Results.Ok(await svc.ArchiveDecompressAsync(archivePath, destDir, req.ArchivePass, u, ct));
        });

        // --- Scan (admin-only) ---
        g.MapPost("/scan", async (PathReq req, IScanService scan, ClaimsPrincipal user) =>
        {
            var u = user.ToUser();
            if (!u.IsAdmin())
                return Results.Ok(Resp.Fail("admin only", RespCodes.Forbidden));
            return Results.Ok(await scan.StartAsync(Clean(req.Path)));
        });

        // --- Offline download (Phase 6) ---
        g.MapPost("/add_offline_download", async (OfflineDownloadReq req, IFileSystemService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            req.Path = Clean(req.Path);
            return Results.Ok(await svc.AddOfflineDownloadAsync(req, u, ct));
        });

        // POST /fs/other — driver-specific operations (e.g. aliyun video_preview).
        // Body: { path, password, method, ... }. Delegates to
        // IFileSystemService.OtherAsync(path, method); drivers that don't override
        // it return 501 so the frontend shows "unsupported" cleanly.
        // Go: handles.FsOther via g.Any(...).
        g.MapMethods("/other", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<FsOtherReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            var path = Clean(req.Path);
            // 镜像 Go FsOther：CanAccess(user, meta, path, req.Password)。
            if (await AccessGateAsync(metaSvc, u, path, req.Password, ct) is { } denied) return denied;
            return Results.Ok(await svc.OtherAsync(path, req.Method ?? "", u));
        });

        // --- Torrent endpoints ---
        // /fs/torrent/{parse,rapid_upload,upload_parse,generate} 路由已在 MapTorrentApi 中注册，
        // 此处保留遗留 /torrent/list|info|add|cancel stub 供旧客户端使用。
        g.MapGet("/torrent/list", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapGet("/torrent/info", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/add", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/cancel", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));

        return app;
    }

    /// <summary>
    /// Reads the URL-encoded "File-Path" header (matches Go frontend convention).
    /// Returns null if missing or invalid.
    /// </summary>
    private static string? ReadFilePathHeader(HttpContext ctx)
    {
        var raw = ctx.Request.Headers["File-Path"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return Uri.UnescapeDataString(raw); }
        catch { return null; }
    }

    /// <summary>
    /// Joins a directory with relative names to form full virtual paths.
    /// e.g. dir="/a/b", names=["c","d"] → ["/a/b/c", "/a/b/d"].
    /// </summary>
    internal static IReadOnlyList<string> JoinPaths(string dir, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return [];
        var baseDir = string.IsNullOrEmpty(dir) || dir == "/" ? "" : dir.TrimEnd('/');
        return names.Select(n => baseDir == "" ? $"/{n}" : $"{baseDir}/{n}").ToList();
    }

    /// <summary>
    /// 把端点用的 <see cref="MoveReq"/> 转成服务层用的 <see cref="MoveCopyReq"/>。
    /// </summary>
    internal static MoveCopyReq ToMoveCopy(MoveReq req) => new()
    {
        SrcDir = req.SrcDir,
        DstDir = req.DstDir,
        Names = req.Names,
        Overwrite = req.Overwrite,
        SkipExisting = req.SkipExisting,
        Merge = req.Merge,
    };

    /// <summary>
    /// 把请求路径规整为完整的虚拟路径（补前导斜杠、解析 <c>.</c>/<c>..</c>）。
    /// 单用户产品没有 BasePath，故不存在「越出用户目录」这一失败态。
    /// </summary>
    internal static string Clean(string? reqPath) => UPath.FixAndCleanPath(reqPath);

    /// <summary>路径的父目录（镜像 Go <c>stdpath.Dir</c>）。</summary>
    private static string ParentPath(string path) => MetaAccess.Parent(path);

    /// <summary>
    /// 解析 <paramref name="path"/> 的最近 meta（镜像 Go <c>op.GetNearestMeta</c>）。
    /// 解析失败返回 null 而不抛错 —— meta 故障不应阻断读写。
    /// </summary>
    internal static async Task<Meta?> NearestMetaAsync(IMetaService meta, string path, CancellationToken ct)
    {
        try { return MetaAccess.FindNearest((await meta.ListAsync(ct)).Data ?? [], path); }
        catch { return null; }
    }

    /// <summary>
    /// 只读端点的 meta 访问门禁，镜像 Go <c>FsList</c> / <c>FsGet</c> / <c>FsDirs</c> / <c>FsOther</c>
    /// 内联的 <c>common.CanAccess(user, meta, reqPath, req.Password)</c>：隐藏条目、目录密码
    /// 任一不通过即返回 403。前端在响应体 <c>code=403</c> 时弹出密码输入框，
    /// 因此这里沿用项目惯例（HTTP 200 + 业务码 403），与其它 meta 门禁一致。
    /// </summary>
    private static async Task<IResult?> AccessGateAsync(
        IMetaService meta, User user, string path, string? password, CancellationToken ct)
        => MetaAccess.CanAccess(user, await NearestMetaAsync(meta, path, ct), path, password)
            ? null
            : Results.Ok(Resp.Fail("password is incorrect or you have no permission", RespCodes.Forbidden));
}

// --- 请求 DTO ---
// 字段名通过 [JsonPropertyName] 使用 snake_case 以匹配前端 api.ts 的请求体。

/// <summary>通用路径请求体（/mkdir、/link 等）。</summary>
public sealed class PathReq { public string Path { get; set; } = "/"; }

/// <summary>
/// 删除空目录请求体（<c>/fs/remove_empty_directory</c>）。
/// 同时接受 `path`（本仓库约定）与 `src_dir`（Go 上游约定）；都不传才回落到 `/`。
/// </summary>
public sealed class RemoveEmptyDirectoryReq
{
    public string Path { get; set; } = "";
    [JsonPropertyName("src_dir")]
    public string? SrcDir { get; set; }
}

/// <summary>目录列取请求体（<c>/dirs</c>）。</summary>
public sealed class DirsReq
{
    public string Path { get; set; } = "/";
    public string? Password { get; set; }
    [JsonPropertyName("force_root")]
    public bool ForceRoot { get; set; }
}

/// <summary>重命名请求体（<c>/fs/rename</c>）。</summary>
public sealed class RenameReq
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Overwrite { get; set; }
}

/// <summary>批量重命名请求体（旧版 <c>/fs/rename_batch</c>，Items 为完整路径）。</summary>
public sealed class BatchRenameReq { public List<BatchRenameItem> Items { get; set; } = []; }

/// <summary>前端版批量重命名请求体（<c>/fs/batch_rename</c>，仅文件名 + src_dir）。</summary>
public sealed class FsBatchRenameReq
{
    [JsonPropertyName("src_dir")]
    public string? SrcDir { get; set; }
    [JsonPropertyName("rename_objects")]
    public List<RenameObjDto> RenameObjects { get; set; } = [];
}

/// <summary>正则批量重命名请求体（<c>POST /fs/regex_rename</c>，对应 Go RegexRenameReq）。</summary>
public sealed class RegexRenameReq
{
    [JsonPropertyName("src_dir")]
    public string SrcDir { get; set; } = "";
    [JsonPropertyName("src_name_regex")]
    public string SrcNameRegex { get; set; } = "";
    [JsonPropertyName("new_name_regex")]
    public string NewNameRegex { get; set; } = "";
}
/// <summary>批量重命名单项：旧名 → 新名。</summary>
public sealed class RenameObjDto
{
    [JsonPropertyName("src_name")]
    public string SrcName { get; set; } = "";
    [JsonPropertyName("new_name")]
    public string NewName { get; set; } = "";
}

/// <summary>/fs/move 与 /fs/copy 共用的请求体；前端发送 src_dir + names。</summary>
public sealed class MoveReq
{
    [JsonPropertyName("src_dir")]
    public string SrcDir { get; set; } = "";
    [JsonPropertyName("dst_dir")]
    public string DstDir { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public bool Overwrite { get; set; }
    [JsonPropertyName("skip_existing")]
    public bool SkipExisting { get; set; }
    public bool Merge { get; set; }
}

/// <summary>删除请求体（<c>/fs/remove</c>）。</summary>
public sealed class RemoveReq
{
    public string Dir { get; set; } = "";
    public List<string> Names { get; set; } = [];
}

/// <summary>归档列表请求体（<c>/fs/archive/list</c>）。</summary>
public sealed class ArchiveListReq
{
    public string Path { get; set; } = "";
    public string? Password { get; set; }
    [JsonPropertyName("archive_pass")]
    public string? ArchivePass { get; set; }
    [JsonPropertyName("inner_path")]
    public string InnerPath { get; set; } = "/";
    public int Page { get; set; } = 1;
    [JsonPropertyName("per_page")]
    public int PerPage { get; set; } = 0;
    public bool Refresh { get; set; }
}

/// <summary>归档解压请求体（<c>/fs/archive/decompress</c>）。</summary>
public sealed class ArchiveDecompressReq
{
    [JsonPropertyName("src_dir")]
    public string SrcDir { get; set; } = "";
    [JsonPropertyName("dst_dir")]
    public string DstDir { get; set; } = "";
    public List<string> Name { get; set; } = [];
    [JsonPropertyName("archive_pass")]
    public string? ArchivePass { get; set; }
    [JsonPropertyName("inner_path")]
    public string InnerPath { get; set; } = "/";
    [JsonPropertyName("cache_full")]
    public bool CacheFull { get; set; } = true;
    [JsonPropertyName("put_into_new_dir")]
    public bool PutIntoNewDir { get; set; }
    public bool Overwrite { get; set; }
}

/// <summary>获取直传（预签 URL）信息的请求体（<c>POST /fs/get_direct_upload_info</c>，对应 Go <c>FsGetDirectUploadInfoReq</c>）。</summary>
public sealed class DirectUploadInfoReq
{
    public string Path { get; set; } = "";
    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }
    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }
    public string? Tool { get; set; }
}

/// <summary>递归移动请求体（<c>/fs/recursive_move</c>）。</summary>
public sealed class RecursiveMoveReq
{
    [JsonPropertyName("src_dir")]
    public string SrcDir { get; set; } = "";
    [JsonPropertyName("dst_dir")]
    public string DstDir { get; set; } = "";
    [JsonPropertyName("conflict_policy")]
    public int ConflictPolicy { get; set; }
}

/// <summary>驱动特有操作请求体（<c>/fs/other</c>，如 aliyun video_preview）。</summary>
public sealed class FsOtherReq { public string Path { get; set; } = "/"; public string? Password { get; set; } public string? Method { get; set; } }
