using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Services;
using Zio;

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
            return await StreamFileAsync(mount, fullPath, ctx);
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
            return await StreamFileAsync(mount, fullPath, ctx);
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

    private static async Task<IResult> StreamFileAsync(ZioMountService mount, string path, HttpContext ctx)
    {
        var upath = new UPath(path);
        if (!mount.Root.FileExists(upath))
            return Results.NotFound("file not found");

        var entry = mount.Root.GetFileEntry(upath);
        var contentType = GetContentType(entry.Name);

        // Range support for media playback
        var rangeHeader = ctx.Request.Headers.Range.ToString();
        long fileSize = entry.Length;
        long start = 0, end = fileSize - 1;

        if (!string.IsNullOrEmpty(rangeHeader) && RangeHeader.TryParse(rangeHeader, fileSize, out var r))
        {
            (start, end) = r;
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{fileSize}";
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
        }

        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = end - start + 1;
        ctx.Response.Headers.AcceptRanges = "bytes";
        ctx.Response.Headers.LastModified = entry.LastWriteTime.ToString("R");

        await using var stream = mount.Root.OpenFile(upath, FileMode.Open, FileAccess.Read);
        // Seek to start for range requests
        if (start > 0) stream.Seek(start, SeekOrigin.Begin);
        // Copy exactly [start, end] bytes. CopyToAsync's 2nd arg is bufferSize, not byte count —
        // using it as a limit would write past ContentLength and cause
        // "Response Content-Length mismatch" 500s on Range requests.
        var bytesToCopy = end - start + 1;
        var buffer = new byte[81920];
        long remaining = bytesToCopy;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ctx.RequestAborted);
            if (read <= 0) break;
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            remaining -= read;
        }
        return Results.Empty;
    }

    private static string GetContentType(string fileName)
    {
        var provider = new FileExtensionContentTypeProvider();
        return provider.TryGetContentType(fileName, out var ct) ? ct : "application/octet-stream";
    }

    private static class RangeHeader
    {
        public static bool TryParse(string header, long fileSize, out (long start, long end) range)
        {
            range = (0, fileSize - 1);
            if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            var spec = header[6..].Trim();
            var dash = spec.IndexOf('-');
            if (dash < 0) return false;
            var startStr = spec[..dash];
            var endStr = spec[(dash + 1)..];
            long start, end;
            if (string.IsNullOrEmpty(startStr))
            {
                // suffix range: bytes=-N (last N bytes)
                if (!long.TryParse(endStr, out var suffix)) return false;
                start = Math.Max(0, fileSize - suffix);
                end = fileSize - 1;
            }
            else
            {
                if (!long.TryParse(startStr, out start)) return false;
                end = string.IsNullOrEmpty(endStr) ? fileSize - 1 : (long.TryParse(endStr, out end) ? end : fileSize - 1);
            }
            if (start > end || start >= fileSize) return false;
            if (end >= fileSize) end = fileSize - 1;
            range = (start, end);
            return true;
        }
    }
}
