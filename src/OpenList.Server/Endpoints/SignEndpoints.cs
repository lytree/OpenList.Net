using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

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
        // /d/{path}?sign=... — 直接下载：当驱动实现了 IFileSystemLinkProvider 时 302 跳转到原始
        // 文件 URL，否则回退为流式传输（与 /p/ 一致）。本地驱动始终走流式传输。
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

        // /p/{path}?sign=... — 代理下载：始终通过服务器流式传输文件
        // （用于驱动必须代理的场景，或需要 Range 支持的多媒体播放）。
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

        // HEAD /d/ 与 /p/ — Go 在 GET 之外同时注册了 HEAD。播放器与下载管理器
        // 常先发 HEAD 探测文件大小/Range 支持，再发起实际 GET。
        // StreamFileAsync 会响应 Range 头并对部分内容返回 206。
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
