using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;
using OpenList.Vfs;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 文件系统端点：list、get、mkdir、rename、move、copy、remove、upload、dirs、search 等。
/// 对应 OpenList Go <c>server/handles/fsread.go</c> + <c>fsmanage.go</c>。
/// 大多数端点需登录鉴权；部分只读端点（list/get/dirs/search）与 /fs/other 匿名可访问
/// （用于公开分享浏览），/fs/link 仅管理员。部分读接口兼容 GET+POST。
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

        // --- 读取 ---
        // Go's fsAndShare group allows guest access (Auth(true)) for these read endpoints.
        // AllowAnonymous here lets unauthenticated visitors browse public shares; JWT, if
        // present, is still parsed by JwtBearer so ClaimsPrincipal.ToUser() yields the real
        // user (or a guest User with Id=0/Role=Guest when anonymous).
        // Go registers these via g.Any(...) — accept GET or POST for third-party
        // clients that follow the Go convention (the frontend itself uses POST).
        // POST binds the JSON body; GET binds the DTO from query params.
        g.MapMethods("/list", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<FsListReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
            req.Path = path;
            return Results.Ok(await svc.ListAsync(req, u));
        }).AllowAnonymous();

        g.MapMethods("/get", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<FsGetReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
            req.Path = path;
            return Results.Ok(await svc.GetAsync(req, u));
        }).AllowAnonymous();

        g.MapMethods("/dirs", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<DirsReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            // Go FsDirs：force_root 仅管理员可用且不做 BasePath 拼接；否则套 BasePath。
            string path;
            if (req.ForceRoot)
            {
                if (!u.IsAdmin()) return Results.Ok(Resp.Fail("permission denied", RespCodes.Forbidden));
                path = UPath.FixAndCleanPath(req.Path);
            }
            else if (JoinGuard(u, req.Path, out path) is { } fail) { return fail; }
            return Results.Ok(await svc.DirsAsync(path, u));
        }).AllowAnonymous(); // password accepted but ignored (no driver uses it yet)

        g.MapMethods("/search", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<SearchReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            // Go search.go 对越界相对路径回 400（而非 403），此处保持一致。
            if (!u.TryJoinPath(req.Parent, out var parent))
                return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.BadRequest));
            req.Parent = parent;
            return Results.Ok(await svc.SearchAsync(req, u));
        }).AllowAnonymous();

        // --- Manage ---
        g.MapPost("/mkdir", async (PathReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
            // 镜像 Go FsMkdir：CanWriteContent（或 meta 显式开放写入）+ meta WriteUsers 白名单。
            if (await WriteGateAsync(metaSvc, u, path, ct) is { } denied) return denied;
            return Results.Ok(await svc.MkdirAsync(path, u));
        });

        // POST /fs/rename — body: { path, name, overwrite }. newPath = parent(join(path)) + name.
        // 新名字必须是纯名称（镜像 Go FsRename 的 checkRelativePath(req.Name)）。
        g.MapPost("/rename", async (RenameReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            // 镜像 Go FsRename 的顺序：先 CanRename，再 JoinPath + checkRelativePath，最后父目录 meta 写入校验。
            if (PermissionGuard(u.CanRename()) is { } denied) return denied;
            if (JoinGuard(u, req.Path, out var reqPath) is { } fail) return fail;
            if (!UPath.IsValidName(req.Name))
                return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.Forbidden));
            var parent = ParentPath(reqPath);
            if (await MetaWriteGateAsync(metaSvc, u, parent, ct) is { } metaDenied) return metaDenied;
            var newPath = parent == "/" ? $"/{req.Name}" : $"{parent}/{req.Name}";
            return Results.Ok(await svc.RenameAsync(reqPath, newPath, u));
        });

        // 旧版 API 兼容入口：Items 携带完整路径（非本项目的 Go 对齐面），逐项套用 BasePath。
        g.MapPost("/rename_batch", async (BatchRenameReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            if (PermissionGuard(u.CanRename()) is { } denied) return denied;
            var items = new List<BatchRenameItem>(req.Items.Count);
            foreach (var it in req.Items)
            {
                if (JoinGuard(u, it.SrcName, out var src) is { } f1) return f1;
                if (JoinGuard(u, it.NewName, out var name) is { } f2) return f2;
                if (await MetaWriteGateAsync(metaSvc, u, ParentPath(src), ct) is { } md) return md;
                items.Add(new BatchRenameItem { SrcName = src, NewName = name });
            }
            return Results.Ok(await svc.BatchRenameAsync(items, u));
        });

        // POST /fs/batch_rename — frontend variant. Body: { src_dir, rename_objects: [{src_name, new_name}] }
        // rename_objects carry only file names; src_dir is prepended to form full paths.
        // 名称必须是纯名称（镜像 Go FsBatchRename 的 checkRelativePath）。
        g.MapPost("/batch_rename", async (FsBatchRenameReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            // 镜像 Go FsBatchRename：CanRename + src_dir 的 meta 写入校验。
            if (PermissionGuard(u.CanRename()) is { } denied) return denied;
            if (JoinGuard(u, req.SrcDir, out var dir) is { } fail) return fail;
            if (await MetaWriteGateAsync(metaSvc, u, dir, ct) is { } metaDenied) return metaDenied;
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
            return Results.Ok(await svc.BatchRenameAsync(items, u));
        });

        // POST /fs/regex_rename — Go API completeness (the frontend computes new
        // names client-side and uses /batch_rename instead). Body: { src_dir,
        // src_name_regex, new_name_regex }. Delegates to IFileSystemService.RegexRenameAsync.
        g.MapPost("/regex_rename", async (RegexRenameReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            if (PermissionGuard(u.CanRename()) is { } denied) return denied;
            if (JoinGuard(u, req.SrcDir, out var dir) is { } fail) return fail;
            if (await MetaWriteGateAsync(metaSvc, u, dir, ct) is { } metaDenied) return metaDenied;
            return Results.Ok(await svc.RegexRenameAsync(dir, req.SrcNameRegex, req.NewNameRegex, u));
        });

        // POST /fs/move — taskified, mirrors Go FsMove.
        // Body: { src_dir, dst_dir, names, overwrite, skip_existing }.
        g.MapPost("/move", async (MoveReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            // 镜像 Go FsMove：CanMove + 源目录、目标目录的 meta 写入校验。
            if (PermissionGuard(u.CanMove()) is { } denied) return denied;
            if (JoinGuard(u, req.SrcDir, out var src) is { } f1) return f1;
            if (await MetaWriteGateAsync(metaSvc, u, src, ct) is { } m1) return m1;
            if (JoinGuard(u, req.DstDir, out var dst) is { } f2) return f2;
            if (await MetaWriteGateAsync(metaSvc, u, dst, ct) is { } m2) return m2;
            var mc = ToMoveCopy(req);
            mc.SrcDir = src;
            mc.DstDir = dst;
            return Results.Ok(await svc.MoveBatchAsync(mc, u));
        });

        // POST /fs/copy — taskified with optional merge, mirrors Go FsCopy.
        g.MapPost("/copy", async (MoveReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            // 镜像 Go FsCopy：CanCopy + 源目录的 meta 读权限校验（CanRead）+ 目标目录的 meta 写权限校验（CanWrite）。
            if (PermissionGuard(u.CanCopy()) is { } denied) return denied;
            if (JoinGuard(u, req.SrcDir, out var src) is { } f1) return f1;
            if (await MetaReadGateAsync(metaSvc, u, src, ct) is { } r1) return r1;
            if (JoinGuard(u, req.DstDir, out var dst) is { } f2) return f2;
            if (await MetaWriteGateAsync(metaSvc, u, dst, ct) is { } m2) return m2;
            var mc = ToMoveCopy(req);
            mc.SrcDir = src;
            mc.DstDir = dst;
            return Results.Ok(await svc.CopyBatchAsync(mc, u));
        });

        // POST /fs/remove — body: { dir, names }. paths = join(dir)/names.
        // names 必须是纯名称：含路径成分（/ \ . ..）时返回 403（镜像 Go checkRelativePath）。
        // 否则 UPath 归一化会解析 "\..\.."，拼接后可越出 dir 删除任意文件。
        g.MapPost("/remove", async (RemoveReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (req.Names.Any(n => !UPath.IsValidName(n)))
                return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.Forbidden));
            var u = user.ToUser();
            // 镜像 Go FsRemove：CanRemove + dir 的 meta 写入校验。
            if (PermissionGuard(u.CanRemove()) is { } denied) return denied;
            if (JoinGuard(u, req.Dir, out var dir) is { } fail) return fail;
            if (await MetaWriteGateAsync(metaSvc, u, dir, ct) is { } metaDenied) return metaDenied;
            return Results.Ok(await svc.RemoveAsync(JoinPaths(dir, req.Names), u));
        });

        g.MapPost("/remove_empty_directory", async (PathReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            // 镜像 Go FsRemoveEmptyDirectory：CanRemove + src_dir 的 meta 写入校验。
            if (PermissionGuard(u.CanRemove()) is { } denied) return denied;
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
            if (await MetaWriteGateAsync(metaSvc, u, path, ct) is { } metaDenied) return metaDenied;
            return Results.Ok(await svc.RemoveEmptyDirectoryAsync(path, u));
        });

        // --- Upload ---
        // Go: handles.FsStream + middlewares.FsUp. Path from "File-Path" header (URL-encoded).
        // 套用 BasePath，并补 fsup 的 CanWriteContent + meta 写入校验（无权限返回 403）。
        g.MapPut("/put", async (HttpContext ctx, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user) =>
        {
            var path = ReadFilePathHeader(ctx);
            if (path is null)
                return Results.Ok(Resp.Fail("File-Path header is required", RespCodes.BadRequest));
            var u = user.ToUser();
            if (JoinGuard(u, path, out var joined) is { } fail) return fail;
            if (await WriteGateAsync(metaSvc, u, joined, ctx.RequestAborted) is { } denied) return denied;
            await using var stream = ctx.Request.Body;
            return Results.Ok(await svc.PutStreamAsync(joined, stream, u, ctx.RequestAborted));
        });

        // Go: handles.FsForm + middlewares.FsUp. Multipart form upload. Path from "File-Path" header.
        g.MapPut("/form", async (HttpContext ctx, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user) =>
        {
            var path = ReadFilePathHeader(ctx);
            if (path is null)
                return Results.Ok(Resp.Fail("File-Path header is required", RespCodes.BadRequest));
            var u = user.ToUser();
            if (JoinGuard(u, path, out var joined) is { } fail) return fail;
            if (await WriteGateAsync(metaSvc, u, joined, ctx.RequestAborted) is { } denied) return denied;
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
        // 镜像 Go 门禁：JoinPath → checkRelativePath(file_name) → 目标父目录的 CanWriteContent（或 meta
        // 显式开放写入）+ CanWrite 白名单。任一命中返回 403。
        // 注：Go 另有「父目录与目标挂载点须一致」的守卫（op.GetStorageVirtualMountPath），.NET 无该挂载点
        // 原语，暂未搬（见 AGENTS.md）。
        g.MapPost("/get_direct_upload_info", async (DirectUploadInfoReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
            // file_name 必须是纯名称（镜像 Go FsGetDirectUploadInfo 的 checkRelativePath(req.FileName)）。
            if (!UPath.IsValidName(req.FileName))
                return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.Forbidden));
            // 先把目标路径解析出来，使权限校验与直传能力签发指向同一路径（Go 注释）。纯名称下父目录恒为 path。
            var parentPath = ParentPath($"{path.TrimEnd('/')}/{req.FileName}");
            if (await ContentWriteGateAsync(metaSvc, u, parentPath, ct) is { } denied) return denied;
            return Results.Ok(await svc.GetDirectUploadInfoAsync(path, req.FileSize, u, ct));
        });

        // Go: handles.FsRecursiveMove (alias for move with single src).
        g.MapPost("/recursive_move", async (RecursiveMoveReq req, IFileSystemService svc, IMetaService metaSvc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var u = user.ToUser();
            // 镜像 Go FsRecursiveMove：CanMove + 源目录、目标目录的 meta 写入校验。
            if (PermissionGuard(u.CanMove()) is { } denied) return denied;
            if (JoinGuard(u, req.SrcDir, out var src) is { } f1) return f1;
            if (await MetaWriteGateAsync(metaSvc, u, src, ct) is { } m1) return m1;
            if (JoinGuard(u, req.DstDir, out var dst) is { } f2) return f2;
            if (await MetaWriteGateAsync(metaSvc, u, dst, ct) is { } m2) return m2;
            return Results.Ok(await svc.RecursiveMoveAsync(src, dst, u));
        });

        // --- Link / Archive ---
        // Go: g.POST("/link", middlewares.AuthAdmin, handles.Link) — link generation
        // is admin-only because it can expose storage credentials/direct URLs.
        // 注意：Go 的 Link 刻意不做 BasePath 拼接（注释：请求路径本就是完整路径），此处保持一致。
        g.MapPost("/link", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.LinkAsync(req.Path, user.ToUser()))).RequireAuthorization("admin");

        g.MapMethods("/archive/meta", ["GET", "POST"], async (IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var u = user.ToUser();
            if (JoinGuard(u, "/", out var path) is { } fail) return fail;
            return Results.Ok(await svc.ArchiveMetaAsync(path, u));
        });

        g.MapMethods("/archive/list", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<ArchiveListReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
            return Results.Ok(await svc.ArchiveListAsync(path, req.ArchivePass ?? req.Password, u));
        });

        // POST /fs/archive/decompress — body: { src_dir, dst_dir, name, archive_pass, ... }.
        // archivePath = join(src_dir + name[0]); destPath = join(dst_dir) (+ name folder if put_into_new_dir).
        g.MapPost("/archive/decompress", async (ArchiveDecompressReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var u = user.ToUser();
            var archiveRel = JoinPaths(req.SrcDir, req.Name).FirstOrDefault() ?? req.SrcDir;
            if (JoinGuard(u, archiveRel, out var archivePath) is { } f1) return f1;
            if (JoinGuard(u, req.DstDir, out var destDir) is { } f2) return f2;
            if (req.PutIntoNewDir && req.Name.Count > 0)
            {
                var folder = req.Name[0].TrimEnd('/').Split('/').Last();
                destDir = destDir.TrimEnd('/') + "/" + folder;
            }
            return Results.Ok(await svc.ArchiveDecompressAsync(archivePath, destDir, req.ArchivePass, u));
        });

        // --- Scan (admin-only) ---
        g.MapPost("/scan", async (PathReq req, IScanService scan, ClaimsPrincipal user) =>
        {
            var u = user.ToUser();
            if (!u.IsAdmin())
                return Results.Ok(Resp.Fail("admin only", RespCodes.Forbidden));
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
            return Results.Ok(await scan.StartAsync(path));
        });

        // --- Offline download (Phase 6) ---
        g.MapPost("/add_offline_download", async (OfflineDownloadReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var u = user.ToUser();
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
            req.Path = path;
            return Results.Ok(await svc.AddOfflineDownloadAsync(req, u));
        });

        // POST /fs/other — driver-specific operations (e.g. aliyun video_preview).
        // Body: { path, password, method, ... }. Delegates to
        // IFileSystemService.OtherAsync(path, method); drivers that don't override
        // it return 501 so the frontend shows "unsupported" cleanly.
        // Go: handles.FsOther via g.Any(...).
        g.MapMethods("/other", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<FsOtherReq>(ctx, ctx.RequestAborted);
            var u = user.ToUser();
            if (JoinGuard(u, req.Path, out var path) is { } fail) return fail;
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
    /// 把请求路径套到用户的 <see cref="User.BasePath"/> 之下（镜像 Go <c>User.JoinPath</c>）。
    /// 成功返回 null 并把结果写入 <paramref name="joined"/>；失败（越界相对路径）返回 403
    /// <c>using relative path is not allowed</c>。
    /// </summary>
    internal static IResult? JoinGuard(User user, string? reqPath, out string joined)
    {
        if (user.TryJoinPath(reqPath, out joined)) return null;
        joined = string.Empty;
        return Results.Ok(Resp.Fail("using relative path is not allowed", RespCodes.Forbidden));
    }

    /// <summary>
    /// 权限位门禁：镜像 Go 各管理端点内联的 <c>user.CanXxx()</c> 检查（缺位返回 403 permission denied）。
    /// 返回 null 表示放行。
    /// </summary>
    private static IResult? PermissionGuard(bool allowed)
        => allowed ? null : Results.Ok(Resp.Fail("permission denied", RespCodes.Forbidden));

    /// <summary>
    /// 解析 <paramref name="path"/> 的最近 meta（镜像 Go <c>op.GetNearestMeta</c>）。
    /// 解析失败返回 null 而不抛错 —— 与 Go 内联处「仅 MetaNotFound 可忽略、其余报 500」相比更宽松，
    /// 但与本项目既有 meta 处理（<c>FileSystemService.ListAsync</c>）保持一致，避免 meta 故障阻断写入。
    /// </summary>
    private static async Task<Meta?> NearestMetaAsync(IMetaService meta, string path, CancellationToken ct)
    {
        try { return FindNearestMeta((await meta.ListAsync(ct)).Data ?? [], path); }
        catch { return null; }
    }

    /// <summary>
    /// meta 写权限门禁（镜像 Go <c>common.CanWrite(user, meta, path)</c>）：meta 的 <c>WriteUsers</c>
    /// 白名单非空、不含该用户且 meta 覆盖该路径时返回 403，否则 null。
    /// </summary>
    private static async Task<IResult?> MetaWriteGateAsync(IMetaService meta, User user, string path, CancellationToken ct)
        => CanWrite(user, await NearestMetaAsync(meta, path, ct), path)
            ? null
            : Results.Ok(Resp.Fail("permission denied", RespCodes.Forbidden));

    /// <summary>
    /// meta 读权限门禁（镜像 Go <c>common.CanRead(user, meta, path)</c>）：meta 的 <c>ReadUsers</c>
    /// 白名单非空、不含该用户且 meta 覆盖该路径时返回 403，否则 null。
    /// </summary>
    private static async Task<IResult?> MetaReadGateAsync(IMetaService meta, User user, string path, CancellationToken ct)
        => CanRead(user, await NearestMetaAsync(meta, path, ct), path)
            ? null
            : Results.Ok(Resp.Fail("permission denied", RespCodes.Forbidden));

    /// <summary>
    /// 「权限位 <see cref="User.CanWriteContent"/> 或 meta 显式开放写入」+ <c>WriteUsers</c> 白名单的组合门禁，
    /// meta 直接挂在 <paramref name="path"/> 自身。镜像 Go <c>common.CanWriteContentBypassUserPerms</c> +
    /// <c>common.CanWrite</c> 在 mkdir / fsup / direct_upload 内联处的成对校验。
    /// </summary>
    private static async Task<IResult?> ContentWriteGateAsync(IMetaService meta, User user, string path, CancellationToken ct)
    {
        var nearest = await NearestMetaAsync(meta, path, ct);
        if (!user.CanWriteContent() && !CanWriteContentBypass(nearest, path))
            return Results.Ok(Resp.Fail("permission denied", RespCodes.Forbidden));
        return CanWrite(user, nearest, path)
            ? null
            : Results.Ok(Resp.Fail("permission denied", RespCodes.Forbidden));
    }

    /// <summary>
    /// 上传/写入前置门禁，镜像 Go <c>server/middlewares/fsup.go</c>：校验目标文件<b>父目录</b>
    /// （<c>stdpath.Dir(path)</c>）的权限位与 meta 写入白名单。返回 null 表示放行，否则返回 403。
    /// </summary>
    private static Task<IResult?> WriteGateAsync(IMetaService meta, User user, string path, CancellationToken ct)
        => ContentWriteGateAsync(meta, user, ParentPath(path), ct);

    /// <summary>路径的父目录（镜像 Go <c>stdpath.Dir</c>）。</summary>
    private static string ParentPath(string path)
    {
        var p = UPath.FixAndCleanPath(path).TrimEnd('/');
        var slash = p.LastIndexOf('/');
        return slash <= 0 ? "/" : p[..slash];
    }

    /// <summary>从 meta 列表里取路径前缀最长匹配的那条（镜像 Go <c>op.GetNearestMeta</c>）。</summary>
    private static Meta? FindNearestMeta(IReadOnlyList<Meta> metas, string path)
    {
        Meta? best = null;
        var bestLen = -1;
        foreach (var m in metas)
        {
            if (string.IsNullOrEmpty(m.Path)) continue;
            if (MetaCoversPath(m.Path, path, true) && m.Path.Length > bestLen)
            {
                best = m;
                bestLen = m.Path.Length;
            }
        }
        return best;
    }

    /// <summary>镜像 Go <c>common.CanWriteContentBypassUserPerms</c>：meta 显式开放写入时绕过用户权限位。</summary>
    private static bool CanWriteContentBypass(Meta? meta, string path)
    {
        if (meta is null || !meta.Write) return false;
        var mp = UPath.FixAndCleanPath(meta.Path);
        var p = UPath.FixAndCleanPath(path);
        if (string.Equals(mp, p, StringComparison.OrdinalIgnoreCase)) return true;
        return IsSubPath(mp, p) && meta.WSub;
    }

    /// <summary>镜像 Go <c>common.CanWrite</c>：WriteUsers 白名单命中，或 meta 未覆盖该路径时放行。</summary>
    private static bool CanWrite(User user, Meta? meta, string path)
    {
        if (meta is not null && meta.WriteUsers.Count > 0 && !meta.WriteUsers.Contains(user.Id)
            && MetaCoversPath(meta.Path, path, meta.WriteUsersSub))
            return false;
        return true;
    }

    /// <summary>镜像 Go <c>common.CanRead</c>：ReadUsers 白名单命中，或 meta 未覆盖该路径时放行。</summary>
    private static bool CanRead(User user, Meta? meta, string path)
    {
        if (meta is not null && meta.ReadUsers.Count > 0 && !meta.ReadUsers.Contains(user.Id)
            && MetaCoversPath(meta.Path, path, meta.ReadUsersSub))
            return false;
        return true;
    }

    /// <summary>镜像 Go <c>common.MetaCoversPath</c>：meta 是否覆盖给定路径（可选是否作用于子目录）。</summary>
    private static bool MetaCoversPath(string metaPath, string reqPath, bool applyToSubFolder)
    {
        var mp = UPath.FixAndCleanPath(metaPath);
        var rp = UPath.FixAndCleanPath(reqPath);
        if (string.Equals(mp, rp, StringComparison.OrdinalIgnoreCase)) return true;
        if (!applyToSubFolder) return false;
        while (rp != "/")
        {
            rp = ParentPath(rp);
            if (string.Equals(mp, rp, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>镜像 Go <c>utils.IsSubPath</c>：<paramref name="path"/> 是否位于 <paramref name="parent"/> 之下。</summary>
    private static bool IsSubPath(string parent, string path)
    {
        var p = UPath.FixAndCleanPath(parent).TrimEnd('/');
        if (p.Length == 0) return true; // 根目录覆盖一切
        var c = UPath.FixAndCleanPath(path);
        return c == p || c.StartsWith(p + "/", StringComparison.Ordinal);
    }
}

// --- 请求 DTO ---
// 字段名通过 [JsonPropertyName] 使用 snake_case 以匹配前端 api.ts 的请求体。

/// <summary>通用路径请求体（/mkdir、/remove_empty_directory、/link 等）。</summary>
public sealed class PathReq { public string Path { get; set; } = "/"; }

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
