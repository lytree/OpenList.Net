using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using OpenList.Infrastructure.Services;
using Zio;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Shared file-streaming helper used by signed download (/d/, /p/) and
/// sharing download (/sd/). Supports HTTP Range for media playback.
/// Mirrors OpenList Go <c>server/common/proxy.go</c> stream helpers.
/// </summary>
internal static class FileDownloadHelper
{
    public static async Task<IResult> StreamFileAsync(ZioMountService mount, string path, HttpContext ctx)
    {
        var upath = new UPath(path);
        if (!mount.Root.FileExists(upath))
            return Results.NotFound("file not found");

        var entry = mount.Root.GetFileEntry(upath);
        var contentType = GetContentType(entry.Name);

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
        if (start > 0) stream.Seek(start, SeekOrigin.Begin);
        // Copy exactly [start, end] bytes — CopyToAsync's 2nd arg is bufferSize, not a limit,
        // so writing past ContentLength would cause "Response Content-Length mismatch" 500s.
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

    public static string GetContentType(string fileName)
    {
        var provider = new FileExtensionContentTypeProvider();
        return provider.TryGetContentType(fileName, out var ct) ? ct : "application/octet-stream";
    }

    public static class RangeHeader
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
