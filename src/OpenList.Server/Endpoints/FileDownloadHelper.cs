using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using OpenList.Core.Drivers;
using OpenList.Drivers.Cloud;
using OpenList.Infrastructure.Services;
using Zio;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 共享的文件流式传输辅助类，供签名下载（/d/、/p/）与分享下载（/sd/）复用。
/// 支持 HTTP Range，用于多媒体播放。对应 OpenList Go <c>server/common/proxy.go</c> 的流式辅助。
/// </summary>
internal static class FileDownloadHelper
{
    /// <summary>
    /// 将 Zio 挂载文件系统中的文件以流式输出（含 Content-Type、Accept-Ranges、Last-Modified
    /// 与 Range 206 部分内容响应）。文件不存在时返回 404。
    /// </summary>
    public static async Task<IResult> StreamFileAsync(ZioMountService mount, string path, HttpContext ctx)
    {
        var upath = new UPath(path);
        if (!mount.Root.FileExists(upath))
            return Results.NotFound("file not found");

        var entry = mount.Root.GetFileEntry(upath);
        var contentType = GetContentType(entry.Name);

        // 解析 Range 头：若有效则返回 206 部分内容，否则 200 整文件。
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
        // 精确拷贝 [start, end] 区间字节 —— CopyToAsync 的第二个参数是缓冲区大小而非限制，
        // 若写超 ContentLength 会触发 "Response Content-Length mismatch" 500。
        var bytesToCopy = end - start + 1;
        var buffer = new byte[81920];
        long remaining = bytesToCopy;
        // 分块读取并写出，逐块扣减剩余量，确保既不超发也不漏发。
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ctx.RequestAborted);
            if (read <= 0) break;
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            remaining -= read;
        }
        return Results.Empty;
    }

    /// <summary>
    /// 通过驱动的代理区间读取器输出文件（<c>/p/</c> 上的云盘下载路径）。
    /// <para>
    /// 与 <see cref="StreamFileAsync"/> 的区别：内容不来自 Zio 挂载（Zio 只能顺序读，
    /// 且整份文件的每次读取都要重新打通远端），而是把客户端请求的 Range 直接交给驱动，
    /// 由驱动按区间打开远端流（阿里云盘等还需要在整个响应体生命周期内持有回调并发许可）。
    /// </para>
    /// </summary>
    /// <param name="reader">驱动提供的区间读取器。</param>
    /// <param name="fileSize">远端文件总大小（用于解析 Range 与生成 Content-Range）。</param>
    /// <param name="fileName">文件名（用于推断 Content-Type）。</param>
    /// <param name="lastModified">最后修改时间；未知时传 <see cref="DateTimeOffset.MinValue"/> 以省略该头。</param>
    /// <param name="ctx">当前 HTTP 上下文。</param>
    /// <returns>空结果（响应体已直接写入 <see cref="HttpResponse.Body"/>）。</returns>
    public static async Task<IResult> StreamViaProxyAsync(
        ProxyRangeReader reader,
        long fileSize,
        string fileName,
        DateTimeOffset lastModified,
        HttpContext ctx)
    {
        var rangeHeader = ctx.Request.Headers.Range.ToString();
        long start = 0, end = fileSize - 1;
        var isPartial = false;

        if (!string.IsNullOrEmpty(rangeHeader) && RangeHeader.TryParse(rangeHeader, fileSize, out var r))
        {
            (start, end) = r;
            isPartial = true;
        }

        var length = Math.Max(0, end - start + 1);

        ctx.Response.ContentType = GetContentType(fileName);
        ctx.Response.Headers.AcceptRanges = "bytes";
        if (lastModified > DateTimeOffset.MinValue)
            ctx.Response.Headers.LastModified = lastModified.UtcDateTime.ToString("R");
        if (isPartial)
        {
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{fileSize}";
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
        }
        ctx.Response.ContentLength = length;

        // HEAD 只探测响应头。此处必须提前返回：一旦调用 reader 就会向远端发起真实请求，
        // 对阿里云盘来说等于白占一次回调并发配额（播放器普遍先 HEAD 再 GET）。
        if (HttpMethods.IsHead(ctx.Request.Method)) return Results.Empty;

        Stream stream;
        try
        {
            stream = await reader(start, length, ctx.RequestAborted);
        }
        catch (CallbackCapacityException ex)
        {
            // 回调准入超时/重试用尽 —— 对齐 Go 映射出的 S3 SlowDown，提示客户端稍后重试。
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.RetryAfter = "1";
            return Results.Text(ex.Message);
        }

        await using (stream)
        {
            var buffer = new byte[81920];
            long remaining = length;
            // 逐块扣减剩余量，避免写超 ContentLength（否则框架报 500 内容长度不匹配）。
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ctx.RequestAborted);
                if (read <= 0) break;
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                remaining -= read;
            }
        }
        return Results.Empty;
    }

    /// <summary>根据文件扩展名推断 MIME 类型，未知类型回退为 application/octet-stream。</summary>
    public static string GetContentType(string fileName)
    {
        var provider = new FileExtensionContentTypeProvider();
        return provider.TryGetContentType(fileName, out var ct) ? ct : "application/octet-stream";
    }

    /// <summary>
    /// HTTP Range 头解析器，支持单区间（bytes=start-end / start- / -suffix）。
    /// 解析结果统一夹取到 <c>[0, fileSize-1]</c> 有效范围；无效时返回 false。
    /// </summary>
    public static class RangeHeader
    {
        /// <summary>尝试解析 "bytes=" 形式的 Range 头；成功返回 true 并输出 (start, end)。</summary>
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
                // 后缀区间（bytes=-N）：取文件末尾 N 字节。
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
