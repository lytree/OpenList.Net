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

        g.MapPost("/move", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.MoveAsync(req.SrcPaths, req.DestDir, user.ToUser())));

        g.MapPost("/copy", async (MoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.CopyAsync(req.SrcPaths, req.DestDir, user.ToUser())));

        g.MapPost("/remove", async (RemoveReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RemoveAsync(req.Paths, user.ToUser())));

        g.MapPost("/remove_empty_directory", async (PathReq req, IFileSystemService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.RemoveEmptyDirectoryAsync(req.Path, user.ToUser())));

        // --- Upload ---
        g.MapPut("/put", async (HttpContext ctx, IFileSystemService svc, ClaimsPrincipal user) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (string.IsNullOrEmpty(path))
                return Results.Ok(Resp.Fail("path is required", RespCodes.BadRequest));
            if (ctx.Request.ContentType?.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                foreach (var file in form.Files)
                {
                    await using var stream = file.OpenReadStream();
                    var r = await svc.PutFormAsync(path, stream, file.FileName, user.ToUser(), ctx.RequestAborted);
                    if (r.Code != 200) return Results.Ok(r);
                }
                return Results.Ok(Resp.Ok());
            }
            else
            {
                await using var stream = ctx.Request.Body;
                return Results.Ok(await svc.PutStreamAsync(path, stream, user.ToUser(), ctx.RequestAborted));
            }
        });

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

        return app;
    }
}

// --- Request DTOs ---

public sealed class PathReq { public string Path { get; set; } = "/"; }
public sealed class DirsReq { public string Path { get; set; } = "/"; }
public sealed class RenameReq { public string Path { get; set; } = ""; public string NewPath { get; set; } = ""; }
public sealed class BatchRenameReq { public List<BatchRenameItem> Items { get; set; } = []; }
public sealed class MoveReq { public List<string> SrcPaths { get; set; } = []; public string DestDir { get; set; } = ""; }
public sealed class RemoveReq { public List<string> Paths { get; set; } = []; }
public sealed class ArchiveListReq { public string Path { get; set; } = ""; public string? Password { get; set; } }
public sealed class ArchiveDecompressReq { public string Path { get; set; } = ""; public string DestPath { get; set; } = ""; public string? Password { get; set; } }
