using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using Zio;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 签名 URL 端点：/d/（直接下载）、/p/（代理下载）、
/// /ad/（归档下载/列表）、/ap/（归档代理）、/ae/（归档内部解压）。
/// 对应 OpenList Go <c>server/handles/down.go</c> 与 <c>server/handles/archive.go</c>。
/// 全部公开（无需 JWT），但要求路径携带合法的 HMAC 签名（?sign=）。
/// GET 与 HEAD 均受支持，便于播放器/下载器探测大小与 Range 支持。
/// </summary>
public static class SignEndpoints
{
    /// <summary>签名 URL 端点使用的访客用户（无 JWT，仅凭签名访问）。</summary>
    private static readonly User GuestUser = new() { Id = 0, Username = "", Role = UserRole.Guest };

    /// <summary>
    /// 注册 /d、/p、/ad、/ap、/ae 签名路由（匿名 + 路径签名校验）。
    /// 校验失败返回 401；GET 负责下载/列表/解压，HEAD 用于探测而不产生副作用。
    /// </summary>
    public static IEndpointRouteBuilder MapSignApi(this IEndpointRouteBuilder app)
    {
        // /d/{path}?sign=... — 直接下载：当驱动实现了 IFileSystemLinkProvider 且能给出直链时
        // 302 跳转到原始文件 URL，否则回退为流式传输（与 /p/ 一致）。本地驱动始终走流式传输。
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
            return await DownloadAsync(mount, fullPath, ctx, preferProxy: false);
        }).AllowAnonymous();

        // /p/{path}?sign=... — 代理下载：始终通过服务器流式传输文件
        // （用于驱动必须代理的场景，或需要 Range 支持的多媒体播放）。
        // 驱动能提供区间读取器时（阿里云盘等有回调并发配额的服务）优先走驱动读端，
        // 否则回退为通用 Zio 流式传输。
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
            return await DownloadAsync(mount, fullPath, ctx, preferProxy: true);
        }).AllowAnonymous();

        // HEAD /d/ 与 /p/ — Go 在 GET 之外同时注册了 HEAD。播放器与下载管理器
        // 常先发 HEAD 探测文件大小/Range 支持，再发起实际 GET。
        // 两条路径均只写响应头不读内容（代理路径尤其重要：HEAD 不应消耗远端并发配额）。
        app.MapMethods("/d/{**path}", new[] { "HEAD" }, async (
            string path,
            string? sign,
            ISignService signs,
            ZioMountService mount,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            return await DownloadAsync(mount, fullPath, ctx, preferProxy: false);
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
            return await DownloadAsync(mount, fullPath, ctx, preferProxy: true);
        }).AllowAnonymous();

        // --- 归档签名路由（对齐 Go /ad/、/ap/、/ae/）---
        // /ad/{path}?sign=... — 归档列表：将归档内的条目列表以 JSON 返回。
        // 委托给 IFileSystemService.ArchiveListAsync；当底层驱动未实现
        // IFileSystemArchiveReader 时返回 501（路由与签名已就位，驱动层待接入）。
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

        // /ap/{path}?sign=... — 归档代理：直接流式传输归档文件本身（行为与 /d/ 相同），
        // 但语义上与 /d/ 区分，便于未来对归档流量做差异化限流或日志记录。
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

        // /ae/{path}?sign=... — 归档内部解压：将归档解压到其父目录。
        // 委托给 IFileSystemService.ArchiveDecompressAsync；当驱动未实现
        // IFileSystemArchiveDecompress 时返回 501。
        app.MapGet("/ae/{**path}", async (
            string path,
            string? sign,
            ISignService signs,
            IFileSystemService fs,
            HttpContext ctx) =>
        {
            var fullPath = NormalizePath(path);
            if (!VerifySign(signs, fullPath, sign, ctx)) return Results.Unauthorized();
            // 解压到归档所在目录（对齐 Go 的 ArchiveInternalExtract 行为）。
            var parent = fullPath.TrimEnd('/').LastIndexOf('/');
            var destDir = parent <= 0 ? "/" : fullPath[..parent];
            return Results.Ok(await fs.ArchiveDecompressAsync(fullPath, destDir, null, GuestUser));
        }).AllowAnonymous();

        // HEAD /ad/、/ap/、/ae/ — 仅探测大小/Range 支持，不触发归档逻辑。
        // 注意：此处仍调用 StreamFileAsync，若目标是归档文件则实际输出其二进制流。
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

    /// <summary>
    /// 按驱动能力选择下载方式，是 <c>/d/</c> 与 <c>/p/</c>（含 HEAD）的共用决策逻辑：
    /// </summary>
    /// <list type="number">
    /// <item><c>/d/</c>（<paramref name="preferProxy"/> = false）：驱动给出直链则 302 跳转，
    /// 让客户端直连源站（省服务器带宽）。</item>
    /// <item><c>/p/</c>（<paramref name="preferProxy"/> = true）：驱动提供区间读取器则由服务器
    /// 按请求区间转发（阿里云盘等需要保配额/鉴权的场景）。</item>
    /// <item>以上都不适用时回退到挂载文件系统的通用流式传输（本地驱动、普通网络驱动）。</item>
    /// </list>
    /// <para>
    /// 传给驱动的是**相对挂载点**的路径 —— 驱动的 <c>System</c> 以其挂载点为根，
    /// 与 <c>ZioMountService.ToDriverRelativePath</c> 的约定一致。
    /// </para>
    /// <param name="mount">挂载服务。</param>
    /// <param name="fullPath">请求的完整挂载路径。</param>
    /// <param name="ctx">当前 HTTP 上下文。</param>
    /// <param name="preferProxy">true 走代理通道，false 优先直链。</param>
    private static async Task<IResult> DownloadAsync(
        ZioMountService mount, string fullPath, HttpContext ctx, bool preferProxy)
    {
        if (mount.FindDriverForPath(fullPath) is IFileSystemLinkProvider linkProvider)
        {
            var relative = mount.ToDriverRelativePath(fullPath);
            FileLink? link;
            try
            {
                link = await linkProvider.GetFileLinkAsync(relative, preferProxy, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 取链接失败（远端报错、token 失效等）不应把请求打死 —— 结构上仍可能通过
                // Zio 层读到内容，因此退化为通用流式传输，由它给出最终错误。
                link = null;
            }

            if (!preferProxy && link?.Url is { Length: > 0 } url)
                return Results.Redirect(url, permanent: false);

            if (preferProxy && link?.RangeReader is { } reader)
            {
                var upath = new UPath(fullPath);
                if (mount.Root.FileExists(upath))
                {
                    var entry = mount.Root.GetFileEntry(upath);
                    return await FileDownloadHelper.StreamViaProxyAsync(
                        reader, entry.Length, entry.Name, new DateTimeOffset(entry.LastWriteTime), ctx);
                }
                // 挂载视图里查不到该文件（例如驱动尚未挂载）时同样回退，由通用路径返回 404。
            }
        }

        return await FileDownloadHelper.StreamFileAsync(mount, fullPath, ctx);
    }

    /// <summary>
    /// 规范化路径：空字符串归一化为根路径 "/"，并补充缺失的前导 "/"。
    /// 供签名校验与实际下载使用统一、可比的路径形式。
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return path;
    }

    /// <summary>
    /// 校验请求路径上的 HMAC 签名（?sign=）。签名为空或校验失败均视为无效，
    /// 并在失败时向响应写入 401 状态码。调用方据此返回 Unauthorized。
    /// </summary>
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
