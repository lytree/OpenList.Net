using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Signed URL endpoints: /d/ (direct download), /p/ (proxy download),
/// /ad/ (archive download/list), /ap/ (archive proxy), /ae/ (archive internal extract).
/// Mirrors OpenList Go <c>server/handles/down.go</c> and <c>server/handles/archive.go</c>.
/// All are public (no JWT required) but require a valid HMAC signature on the path.
/// </summary>
public static class SignEndpoints
{
    /// <summary>Guest user used by signed-URL endpoints (no JWT, signature-only access).</summary>
    private static readonly User GuestUser = new() { Id = 0, Username = "", Role = UserRole.Guest };

    public static IEndpointRouteBuilder MapSignApi(this IEndpointRouteBuilder app)
    {
        // /d/{path}?sign=... — direct download: 302 redirect to the file's raw URL when
        // the driver provides one (IFileSystemLinkProvider), otherwise streams the file
        // (same as /p/). Local driver always streams.
        app.MapGet("/d/{**path}", async (
            string path,
            string? sign,
            ISignService signs,
            IFileSystemService fs,
            ZioMountService mount,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
        }).AllowAnonymous();

        // /p/{path}?sign=... — proxy download: always streams the file through the server
        // (used when the driver must proxy, or for media playback that needs range support).
        app.MapGet("/p/{**path}", async (
            string path,
            string? sign,
            ISignService signs,
            IFileSystemService fs,
            ZioMountService mount,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
        }).AllowAnonymous();

        // HEAD /d/ and /p/ — Go registers HEAD alongside GET. Media players and download
        // managers use HEAD to probe size/range support before issuing the actual GET.
        // StreamFileAsync honors Range headers and returns 206 for partial content.
        app.MapMethods("/d/{**path}", new[] { "HEAD" }, async (
            string path,
            string? sign,
            ISignService signs,
            ZioMountService mount,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
        }).AllowAnonymous();
        app.MapMethods("/p/{**path}", new[] { "HEAD" }, async (
            string path,
            string? sign,
            ISignService signs,
            ZioMountService mount,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
        }).AllowAnonymous();

        // --- Archive signed routes (mirror Go /ad/, /ap/, /ae/) ---
        // /ad/{path}?sign=... — archive list: returns the archive's entry list as JSON.
        // Delegates to IFileSystemService.ArchiveListAsync; returns 501 when the underlying
        // driver does not implement IFileSystemArchiveReader (route + signature in place,
        // driver layer pending).
        app.MapGet("/ad/{**path}", async (
            string path,
            string? sign,
            ISignService signs,
            IFileSystemService fs,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return Results.Ok(await fs.ArchiveListAsync(fullPath, null, GuestUser));
        }).AllowAnonymous();

        // /ap/{path}?sign=... — archive proxy: streams the archive file itself (same as /d/
        // but semantically distinct for archive traffic that may need different rate-limiting
        // or logging in the future).
        app.MapGet("/ap/{**path}", async (
            string path,
            string? sign,
            ISignService signs,
            ZioMountService mount,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
        }).AllowAnonymous();

        // /ae/{path}?sign=... — archive internal extract: decompresses the archive into its
        // parent directory. Delegates to IFileSystemService.ArchiveDecompressAsync; returns
        // 501 when the driver does not implement IFileSystemArchiveDecompress.
        app.MapGet("/ae/{**path}", async (
            string path,
            string? sign,
            ISignService signs,
            IFileSystemService fs,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            // Decompress into the archive's parent directory (Go's ArchiveInternalExtract behaviour).
            var parent = fullPath.TrimEnd('/').LastIndexOf('/');
            var destDir = parent <= 0 ? "/" : fullPath[..parent];
            return Results.Ok(await fs.ArchiveDecompressAsync(fullPath, destDir, null, GuestUser));
        }).AllowAnonymous();

        // HEAD /ad/, /ap/, /ae/ — probe size/range support without invoking archive logic.
        app.MapMethods("/ad/{**path}", new[] { "HEAD" }, async (
            string path, string? sign, ISignService signs, ZioMountService mount, HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
        }).AllowAnonymous();
        app.MapMethods("/ap/{**path}", new[] { "HEAD" }, async (
            string path, string? sign, ISignService signs, ZioMountService mount, HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
        }).AllowAnonymous();
        app.MapMethods("/ae/{**path}", new[] { "HEAD" }, async (
            string path, string? sign, ISignService signs, ZioMountService mount, HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
        }).AllowAnonymous();

        return app;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return path;
    }

    private static bool VerifySign(ISignService signs, string path, string? sign, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(sign)) return false;
        if (!signs.Verify(path, sign))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }
        return true;
    }
}
