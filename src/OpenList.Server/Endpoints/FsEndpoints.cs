using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Filesystem endpoints: list, get, mkdir, rename, move, copy, remove, upload, dirs, search.
/// Mirrors OpenList Go <c>server/handles/fsread.go</c> + <c>fsmanage.go</c>.
/// All endpoints require authentication.
/// </summary>
public static class FsEndpoints
{
    public static IEndpointRouteBuilder MapFsApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/fs").WithTags("Fs").RequireAuthorization();

        // --- Read ---
        g.MapPost("/list", async (FsListReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ListAsync(req, user.ToUser())));

        g.MapPost("/get", async (FsGetReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.GetAsync(req, user.ToUser())));

        g.MapPost("/dirs", async (DirsReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.DirsAsync(req.Path, user.ToUser())));

        g.MapPost("/search", async (SearchReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.SearchAsync(req, user.ToUser())));

        // --- Manage ---
        g.MapPost("/mkdir", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.MkdirAsync(req.Path, user.ToUser())));

        g.MapPost("/rename", async (RenameReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RenameAsync(req.Path, req.NewPath, user.ToUser())));

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

        g.MapPost("/move", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.MoveAsync(req.SrcPaths, req.DestDir, user.ToUser())));

        g.MapPost("/copy", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.CopyAsync(req.SrcPaths, req.DestDir, user.ToUser())));

        g.MapPost("/remove", async (RemoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RemoveAsync(req.Paths, user.ToUser())));

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
        g.MapPost("/link", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.LinkAsync(req.Path, user.ToUser())));

        g.MapGet("/archive/meta", async (IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ArchiveMetaAsync("/", user.ToUser())));

        g.MapPost("/archive/list", async (ArchiveListReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ArchiveListAsync(req.Path, req.Password, user.ToUser())));

        g.MapPost("/archive/decompress", async (ArchiveDecompressReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ArchiveDecompressAsync(req.Path, req.DestPath, req.Password, user.ToUser())));

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
        g.MapPost("/other", async (FsOtherReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.OtherAsync(req.Path, req.Method ?? "", user.ToUser())));

        // --- Torrent endpoints (Phase 6 — not yet implemented, return 501) ---
        // Route names match the Go reference + frontend api.ts:
        //   /fs/torrent/parse         (POST, body: { torrent_data })
        //   /fs/torrent/upload_parse  (POST, multipart "torrent")
        //   /fs/torrent/rapid_upload  (POST, body: { torrent_data, path })
        // Legacy /torrent/list|info|add|cancel stubs retained for older clients.
        g.MapGet("/torrent/list", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapGet("/torrent/info", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/add", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/cancel", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/parse", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/upload_parse", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/rapid_upload", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));

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
}

// --- Request DTOs ---

public sealed class PathReq { public string Path { get; set; } = "/"; }
public sealed class DirsReq { public string Path { get; set; } = "/"; }
public sealed class RenameReq { public string Path { get; set; } = ""; public string NewPath { get; set; } = ""; }
public sealed class BatchRenameReq { public List<BatchRenameItem> Items { get; set; } = []; }
public sealed class FsBatchRenameReq
{
    public string? SrcDir { get; set; }
    public List<RenameObjDto> RenameObjects { get; set; } = [];
}
public sealed class RenameObjDto
{
    public string SrcName { get; set; } = "";
    public string NewName { get; set; } = "";
}
public sealed class MoveReq { public List<string> SrcPaths { get; set; } = []; public string DestDir { get; set; } = ""; }
public sealed class RemoveReq { public List<string> Paths { get; set; } = []; }
public sealed class ArchiveListReq { public string Path { get; set; } = ""; public string? Password { get; set; } }
public sealed class ArchiveDecompressReq { public string Path { get; set; } = ""; public string DestPath { get; set; } = ""; public string? Password { get; set; } }
public sealed class DirectUploadInfoReq { public string Path { get; set; } = ""; public string? FileName { get; set; } public long FileSize { get; set; } public string? Tool { get; set; } }
public sealed class RecursiveMoveReq { public string SrcDir { get; set; } = ""; public string DstDir { get; set; } = ""; }
public sealed class FsOtherReq { public string Path { get; set; } = "/"; public string? Password { get; set; } public string? Method { get; set; } }
