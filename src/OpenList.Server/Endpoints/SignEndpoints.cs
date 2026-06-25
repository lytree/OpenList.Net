using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Signed URL endpoints: /d/ (direct download) and /p/ (proxy download).
/// Mirrors OpenList Go <c>server/handles/down.go</c>.
/// Both are public (no JWT required) but require a valid HMAC signature on the path.
/// </summary>
public static class SignEndpoints
{
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
