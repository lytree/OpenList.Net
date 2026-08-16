using System.Security.Claims;
using System.Text.Json.Serialization;
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
        // Go's fsAndShare group allows guest access (Auth(true)) for these read endpoints.
        // AllowAnonymous here lets unauthenticated visitors browse public shares; JWT, if
        // present, is still parsed by JwtBearer so ClaimsPrincipal.ToUser() yields the real
        // user (or a guest User with Id=0/Role=Guest when anonymous).
        g.MapPost("/list", async (FsListReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ListAsync(req, user.ToUser()))).AllowAnonymous();

        g.MapPost("/get", async (FsGetReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.GetAsync(req, user.ToUser()))).AllowAnonymous();

        g.MapPost("/dirs", async (DirsReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.DirsAsync(req.Path, user.ToUser()))).AllowAnonymous(); // password/force_root accepted but ignored (no driver uses them yet)

        g.MapPost("/search", async (SearchReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.SearchAsync(req, user.ToUser()))).AllowAnonymous();

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

        // POST /fs/move — body: { src_dir, dst_dir, names, overwrite, skip_existing }.
        // srcPaths = names.Select(n => src_dir/n).
        g.MapPost("/move", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.MoveAsync(JoinPaths(req.SrcDir, req.Names), req.DstDir, user.ToUser())));

        // POST /fs/copy — body: { src_dir, dst_dir, names, overwrite, skip_existing, merge }.
        g.MapPost("/copy", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.CopyAsync(JoinPaths(req.SrcDir, req.Names), req.DstDir, user.ToUser())));

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
        g.MapPost("/link", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.LinkAsync(req.Path, user.ToUser())));

        g.MapGet("/archive/meta", async (IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ArchiveMetaAsync("/", user.ToUser())));

        g.MapPost("/archive/list", async (ArchiveListReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.ArchiveListAsync(req.Path, req.ArchivePass ?? req.Password, user.ToUser())));

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
        g.MapPost("/other", async (FsOtherReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.OtherAsync(req.Path, req.Method ?? "", user.ToUser())));

        // --- Torrent endpoints (Phase 6 — not yet implemented, return 501) ---
        // Route names match the Go reference + frontend api.ts:
        //   /fs/torrent/parse         (POST, body: { torrent_data })
        //   /fs/torrent/upload_parse  (POST, multipart "torrent")
        //   /fs/torrent/rapid_upload  (POST, body: { torrent_data, path })
        //   /fs/torrent/generate      (POST, body: { path }) — generate torrent for path
        // Legacy /torrent/list|info|add|cancel stubs retained for older clients.
        g.MapGet("/torrent/list", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapGet("/torrent/info", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/add", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/cancel", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/parse", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/upload_parse", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/rapid_upload", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));
        g.MapPost("/torrent/generate", () => Results.Ok(Resp.Fail("torrent not implemented", RespCodes.NotImplemented)));

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
}

// --- Request DTOs ---
// Field names use snake_case via [JsonPropertyName] to match the frontend's api.ts payloads.

public sealed class PathReq { public string Path { get; set; } = "/"; }

public sealed class DirsReq
{
    public string Path { get; set; } = "/";
    public string? Password { get; set; }
    [JsonPropertyName("force_root")]
    public bool ForceRoot { get; set; }
}

public sealed class RenameReq
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Overwrite { get; set; }
}

public sealed class BatchRenameReq { public List<BatchRenameItem> Items { get; set; } = []; }

public sealed class FsBatchRenameReq
{
    [JsonPropertyName("src_dir")]
    public string? SrcDir { get; set; }
    [JsonPropertyName("rename_objects")]
    public List<RenameObjDto> RenameObjects { get; set; } = [];
}
public sealed class RenameObjDto
{
    [JsonPropertyName("src_name")]
    public string SrcName { get; set; } = "";
    [JsonPropertyName("new_name")]
    public string NewName { get; set; } = "";
}

/// <summary>Shared body for /fs/move and /fs/copy. Frontend sends src_dir + names.</summary>
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

public sealed class RemoveReq
{
    public string Dir { get; set; } = "";
    public List<string> Names { get; set; } = [];
}

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

public sealed class DirectUploadInfoReq { public string Path { get; set; } = ""; public string? FileName { get; set; } public long FileSize { get; set; } public string? Tool { get; set; } }

public sealed class RecursiveMoveReq
{
    [JsonPropertyName("src_dir")]
    public string SrcDir { get; set; } = "";
    [JsonPropertyName("dst_dir")]
    public string DstDir { get; set; } = "";
    [JsonPropertyName("conflict_policy")]
    public int ConflictPolicy { get; set; }
}

public sealed class FsOtherReq { public string Path { get; set; } = "/"; public string? Password { get; set; } public string? Method { get; set; } }
