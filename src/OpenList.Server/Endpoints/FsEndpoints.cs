using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

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
            return Results.Ok(await svc.ListAsync(req, user.ToUser()));
        }).AllowAnonymous();

        g.MapMethods("/get", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<FsGetReq>(ctx, ctx.RequestAborted);
            return Results.Ok(await svc.GetAsync(req, user.ToUser()));
        }).AllowAnonymous();

        g.MapMethods("/dirs", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<DirsReq>(ctx, ctx.RequestAborted);
            return Results.Ok(await svc.DirsAsync(req.Path, user.ToUser()));
        }).AllowAnonymous(); // password/force_root accepted but ignored (no driver uses them yet)

        g.MapMethods("/search", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<SearchReq>(ctx, ctx.RequestAborted);
            return Results.Ok(await svc.SearchAsync(req, user.ToUser()));
        }).AllowAnonymous();

        // --- Manage ---
        g.MapPost("/mkdir", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.MkdirAsync(req.Path, user.ToUser())));

        // POST /fs/rename — body: { path, name, overwrite }. newPath = parent(path) + name.
        g.MapPost("/rename", async (RenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var trimmed = req.Path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            var parent = slash <= 0 ? "/" : trimmed[..slash];
            var newPath = parent == "/" ? $"/{req.Name}" : $"{parent}/{req.Name}";
            return Results.Ok(await svc.RenameAsync(req.Path, newPath, user.ToUser()));
        });

        g.MapPost("/rename_batch", async (BatchRenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.BatchRenameAsync(req.Items, user.ToUser())));

        // POST /fs/batch_rename — frontend variant. Body: { src_dir, rename_objects: [{src_name, new_name}] }
        // rename_objects carry only file names; src_dir is prepended to form full paths.
        g.MapPost("/batch_rename", async (FsBatchRenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var dir = req.SrcDir?.TrimEnd('/') ?? "/";
            var items = req.RenameObjects.Select(o => new BatchRenameItem
            {
                SrcName = string.IsNullOrEmpty(dir) || dir == "/" ? o.SrcName : $"{dir}/{o.SrcName}",
                NewName = string.IsNullOrEmpty(dir) || dir == "/" ? o.NewName : $"{dir}/{o.NewName}",
            }).ToList();
            return Results.Ok(await svc.BatchRenameAsync(items, user.ToUser()));
        });

        // POST /fs/regex_rename — Go API completeness (the frontend computes new
        // names client-side and uses /batch_rename instead). Body: { src_dir,
        // src_name_regex, new_name_regex }. Delegates to IFileSystemService.RegexRenameAsync.
        g.MapPost("/regex_rename", async (RegexRenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RegexRenameAsync(req.SrcDir, req.SrcNameRegex, req.NewNameRegex, user.ToUser())));

        // POST /fs/move — taskified, mirrors Go FsMove.
        // Body: { src_dir, dst_dir, names, overwrite, skip_existing }.
        g.MapPost("/move", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.MoveBatchAsync(ToMoveCopy(req), user.ToUser())));

        // POST /fs/copy — taskified with optional merge, mirrors Go FsCopy.
        g.MapPost("/copy", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.CopyBatchAsync(ToMoveCopy(req), user.ToUser())));

        // POST /fs/remove — body: { dir, names }. paths = names.Select(n => dir/n).
        g.MapPost("/remove", async (RemoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RemoveAsync(JoinPaths(req.Dir, req.Names), user.ToUser())));

        g.MapPost("/remove_empty_directory", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RemoveEmptyDirectoryAsync(req.Path, user.ToUser())));

        // --- Upload ---
        // Go: handles.FsStream. Path from "File-Path" header (URL-encoded), like the frontend.
        g.MapPut("/put", async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var path = ReadFilePathHeader(ctx);
            if (path is null)
                return Results.Ok(Resp.Fail("File-Path header is required", RespCodes.BadRequest));
            await using var stream = ctx.Request.Body;
            return Results.Ok(await svc.PutStreamAsync(path, stream, user.ToUser(), ctx.RequestAborted));
        });

        // Go: handles.FsForm. Multipart form upload. Path from "File-Path" header.
        g.MapPut("/form", async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var path = ReadFilePathHeader(ctx);
            if (path is null)
                return Results.Ok(Resp.Fail("File-Path header is required", RespCodes.BadRequest));
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                var r = await svc.PutFormAsync(path, stream, file.FileName, user.ToUser(), ctx.RequestAborted);
                if (r.Code != 200) return Results.Ok(r);
            }
            return Results.Ok(Resp.Ok());
        });

        // Go: handles.FsGetDirectUploadInfo. Returns presigned upload URL if driver supports it.
        g.MapPost("/get_direct_upload_info", async (DirectUploadInfoReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.GetDirectUploadInfoAsync(req.Path, req.FileSize, user.ToUser())));

        // Go: handles.FsRecursiveMove (alias for move with single src).
        g.MapPost("/recursive_move", async (RecursiveMoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RecursiveMoveAsync(req.SrcDir, req.DstDir, user.ToUser())));

        // --- Link / Archive ---
        // Go: g.POST("/link", middlewares.AuthAdmin, handles.Link) — link generation
        // is admin-only because it can expose storage credentials/direct URLs.
        g.MapPost("/link", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.LinkAsync(req.Path, user.ToUser()))).RequireAuthorization("admin");

        g.MapMethods("/archive/meta", ["GET", "POST"], async (IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ArchiveMetaAsync("/", user.ToUser())));

        g.MapMethods("/archive/list", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<ArchiveListReq>(ctx, ctx.RequestAborted);
            return Results.Ok(await svc.ArchiveListAsync(req.Path, req.ArchivePass ?? req.Password, user.ToUser()));
        });

        // POST /fs/archive/decompress — body: { src_dir, dst_dir, name, archive_pass, ... }.
        // archivePath = src_dir + name[0]; destPath = dst_dir (+ name folder if put_into_new_dir).
        g.MapPost("/archive/decompress", async (ArchiveDecompressReq req, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var archivePath = JoinPaths(req.SrcDir, req.Name).FirstOrDefault() ?? req.SrcDir;
            var destDir = req.DstDir;
            if (req.PutIntoNewDir && req.Name.Count > 0)
            {
                var folder = req.Name[0].TrimEnd('/').Split('/').Last();
                destDir = destDir.TrimEnd('/') + "/" + folder;
            }
            return Results.Ok(await svc.ArchiveDecompressAsync(archivePath, destDir, req.ArchivePass, user.ToUser()));
        });

        // --- Scan (admin-only) ---
        g.MapPost("/scan", async (PathReq req, IScanService scan, ClaimsPrincipal user) =>
        {
            if (!user.IsAdmin())
                return Results.Ok(Resp.Fail("admin only", RespCodes.Forbidden));
            return Results.Ok(await scan.StartAsync(req.Path));
        });

        // --- Offline download (Phase 6) ---
        g.MapPost("/add_offline_download", async (OfflineDownloadReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.AddOfflineDownloadAsync(req, user.ToUser())));

        // POST /fs/other — driver-specific operations (e.g. aliyun video_preview).
        // Body: { path, password, method, ... }. Delegates to
        // IFileSystemService.OtherAsync(path, method); drivers that don't override
        // it return 501 so the frontend shows "unsupported" cleanly.
        // Go: handles.FsOther via g.Any(...).
        g.MapMethods("/other", ["GET", "POST"], async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var req = await RequestBinder.BodyOrQueryAsync<FsOtherReq>(ctx, ctx.RequestAborted);
            return Results.Ok(await svc.OtherAsync(req.Path, req.Method ?? "", user.ToUser()));
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

/// <summary>获取直传（预签 URL）信息的请求体（<c>/fs/get_direct_upload_info</c>）。</summary>
public sealed class DirectUploadInfoReq { public string Path { get; set; } = ""; public string? FileName { get; set; } public long FileSize { get; set; } public string? Tool { get; set; } }

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
