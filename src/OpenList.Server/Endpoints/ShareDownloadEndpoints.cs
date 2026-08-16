using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Sharing signed-download endpoints: /sd/{sid}[/{**path}].
/// Mirrors OpenList Go <c>server/handles/sharing.go</c> DownShare.
/// Public (no JWT) — access is gated by the share code (sid), optional
/// password (?pwd=), expiration, disable flag, and max-accessed counter.
/// </summary>
public static class ShareDownloadEndpoints
{
    public static IEndpointRouteBuilder MapSharingDownloadApi(this IEndpointRouteBuilder app)
    {
        // GET/HEAD /sd/{sid}[/{**path}]?pwd=... — stream a file from a share.
        // The frontend builds URLs like /sd/{shareCode}/{relativePath} (see useLink.ts).
        // The actual fs path = share.Path (root) + relativePath.
        app.MapGet("/sd/{sid}/{**path}", HandleAsync).AllowAnonymous();
        app.MapGet("/sd/{sid}", HandleAsync).AllowAnonymous();
        app.MapMethods("/sd/{sid}/{**path}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();
        app.MapMethods("/sd/{sid}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();

        // GET/HEAD /sad/{sid}[/{**path}]?pwd=... — archive extract from a share.
        // Mirrors Go's SharingArchiveExtract. Verifies the share (same as /sd/), then
        // delegates to IFileSystemService.ArchiveDecompressAsync. Returns 501 when the
        // driver does not implement IFileSystemArchiveDecompress (route in place, driver
        // layer pending).
        // HEAD probes the archive file size via the same /sd/ stream handler (no decompression
        // side-effect on HEAD).
        app.MapGet("/sad/{sid}/{**path}", HandleArchiveAsync).AllowAnonymous();
        app.MapGet("/sad/{sid}", HandleArchiveAsync).AllowAnonymous();
        app.MapMethods("/sad/{sid}/{**path}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();
        app.MapMethods("/sad/{sid}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();
        return app;
    }

    private static readonly User GuestUser = new() { Id = 0, Username = "", Role = UserRole.Guest };

    private static async Task<IResult> HandleArchiveAsync(
        HttpContext ctx,
        ISharingService shares,
        IFileSystemService fs)
    {
        var sid = ctx.GetRouteValue("sid")?.ToString();
        if (string.IsNullOrEmpty(sid))
            return Results.NotFound();

        var pwd = ctx.Request.Query["pwd"].ToString();
        var verify = await shares.VerifyAsync(sid, string.IsNullOrEmpty(pwd) ? null : pwd, ctx.RequestAborted);
        if (verify.Code != RespCodes.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Results.Json(verify, statusCode: StatusCodes.Status403Forbidden);
        }
        var share = verify.Data!;

        // Resolve archive path (same logic as /sd/).
        var relPath = ctx.GetRouteValue("path")?.ToString() ?? string.Empty;
        var actualPath = ResolveActualPath(share, relPath);
        if (actualPath is null)
            return Results.NotFound("path not in share");

        // Decompress into the archive's parent directory (Go's SharingArchiveExtract behaviour).
        var parent = actualPath.TrimEnd('/').LastIndexOf('/');
        var destDir = parent <= 0 ? "/" : actualPath[..parent];
        return Results.Ok(await fs.ArchiveDecompressAsync(actualPath, destDir, null, GuestUser));
    }

    private static async Task<IResult> HandleAsync(
        HttpContext ctx,
        ISharingService shares,
        ZioMountService mount)
    {
        var sid = ctx.GetRouteValue("sid")?.ToString();
        if (string.IsNullOrEmpty(sid))
            return Results.NotFound();

        // Verify share: exists, not disabled, not expired, password matches, under max-accessed.
        var pwd = ctx.Request.Query["pwd"].ToString();
        var verify = await shares.VerifyAsync(sid, string.IsNullOrEmpty(pwd) ? null : pwd, ctx.RequestAborted);
        if (verify.Code != RespCodes.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Results.Json(verify, statusCode: StatusCodes.Status403Forbidden);
        }
        var share = verify.Data!;

        // Resolve actual fs path: share root + relative path from URL.
        var relPath = ctx.GetRouteValue("path")?.ToString() ?? string.Empty;
        var actualPath = ResolveActualPath(share, relPath);
        if (actualPath is null)
            return Results.NotFound("path not in share");

        return await FileDownloadHelper.StreamFileAsync(mount, actualPath, ctx);
    }

    /// <summary>
    /// Map a share-relative URL path to the real fs path. For single-file/single-dir
    /// shares (the common P1 case), this is share.Path + relPath. Multi-file shares
    /// match relPath against the Files list (P2).
    /// </summary>
    private static string? ResolveActualPath(OpenList.Core.Models.Sharing share, string relPath)
    {
        relPath = relPath.Replace('\\', '/').TrimStart('/');
        var root = share.Path.TrimEnd('/');

        // Empty relative path → share root itself (single-file share).
        if (string.IsNullOrEmpty(relPath))
            return share.Path;

        // Single-file/dir share: concatenate root + relPath.
        return $"{root}/{relPath}";
    }
}
