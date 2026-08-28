using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 分享签名下载端点：/sd/{sid}[/{**path}] 与 /sad/{sid}[/{**path}]。
/// 对应 OpenList Go <c>server/handles/sharing.go</c> 的 DownShare 与 SharingArchiveExtract。
/// 公开（无需 JWT），仅凭分享码（sid）访问，且受可选密码（?pwd=）、
/// 过期时间、禁用标记与最大访问次数（max-accessed）约束。
/// GET 与 HEAD 均受支持。
/// </summary>
public static class ShareDownloadEndpoints
{
    /// <summary>
    /// 注册 /sd 与 /sad 分享签名下载路由（匿名）。
    /// /sd 流式传输分享内文件；/sad 对分享内归档做解压。二者都先经
    /// <see cref="ISharingService.VerifyAsync"/> 校验分享有效性。
    /// HEAD 复用 /sd 的流式处理器探测大小，不在 HEAD 上产生解压副作用。
    /// </summary>
    public static IEndpointRouteBuilder MapSharingDownloadApi(this IEndpointRouteBuilder app)
    {
        // GET/HEAD /sd/{sid}[/{**path}]?pwd=... — 从分享中流式读取文件。
        // 前端按 /sd/{shareCode}/{relativePath} 拼 URL（见 useLink.ts）。
        // 实际存储路径 = share.Path（根）+ relativePath。
        app.MapGet("/sd/{sid}/{**path}", HandleAsync).AllowAnonymous();
        app.MapGet("/sd/{sid}", HandleAsync).AllowAnonymous();
        app.MapMethods("/sd/{sid}/{**path}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();
        app.MapMethods("/sd/{sid}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();

        // GET/HEAD /sad/{sid}[/{**path}]?pwd=... — 解压分享中的归档。
        // 对齐 Go 的 SharingArchiveExtract：先与 /sd/ 相同方式校验分享，再委托
        // IFileSystemService.ArchiveDecompressAsync。当驱动未实现
        // IFileSystemArchiveDecompress 时返回 501（路由已就位，驱动层待接入）。
        // HEAD 通过 /sd/ 的流式处理器探测归档大小（HEAD 不产生解压副作用）。
        app.MapGet("/sad/{sid}/{**path}", HandleArchiveAsync).AllowAnonymous();
        app.MapGet("/sad/{sid}", HandleArchiveAsync).AllowAnonymous();
        app.MapMethods("/sad/{sid}/{**path}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();
        app.MapMethods("/sad/{sid}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();
        return app;
    }

    /// <summary>分享签名下载使用的访客用户（无 JWT，仅凭分享校验访问）。</summary>
    private static readonly User GuestUser = new() { Id = 0, Username = "", Role = UserRole.Guest };

    /// <summary>
    /// 处理 /sad 归档解压：校验分享后，解析归档在存储中的实际路径，解压到其父目录。
    /// 鉴权失败返回 403；解析出的路径不在分享范围内返回 404。
    /// </summary>
    private static async Task<IResult> HandleArchiveAsync(
        HttpContext ctx,
        ISharingService shares,
        IFileSystemService fs)
    {
        var sid = ctx.GetRouteValue("sid")?.ToString();
        if (string.IsNullOrEmpty(sid))
            return Results.NotFound();

        // 校验分享：存在、未禁用、未过期、密码匹配、访问次数未超限。
        var pwd = ctx.Request.Query["pwd"].ToString();
        var verify = await shares.VerifyAsync(sid, string.IsNullOrEmpty(pwd) ? null : pwd, ctx.RequestAborted);
        if (verify.Code != RespCodes.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Results.Json(verify, statusCode: StatusCodes.Status403Forbidden);
        }
        var share = verify.Data!;

        // 解析归档路径（与 /sd/ 相同逻辑）。
        var relPath = ctx.GetRouteValue("path")?.ToString() ?? string.Empty;
        var actualPath = ResolveActualPath(share, relPath);
        if (actualPath is null)
            return Results.NotFound("path not in share");

        // 解压到归档所在目录（对齐 Go 的 SharingArchiveExtract 行为）。
        var parent = actualPath.TrimEnd('/').LastIndexOf('/');
        var destDir = parent <= 0 ? "/" : actualPath[..parent];
        return Results.Ok(await fs.ArchiveDecompressAsync(actualPath, destDir, null, GuestUser));
    }

    /// <summary>
    /// 处理 /sd 文件流式下载：校验分享后解析实际路径，委托
    /// <see cref="FileDownloadHelper"/> 流式输出（支持 Range）。鉴权失败返回 403。
    /// </summary>
    private static async Task<IResult> HandleAsync(
        HttpContext ctx,
        ISharingService shares,
        ZioMountService mount)
    {
        var sid = ctx.GetRouteValue("sid")?.ToString();
        if (string.IsNullOrEmpty(sid))
            return Results.NotFound();

        // 校验分享：存在、未禁用、未过期、密码匹配、访问次数未超限。
        var pwd = ctx.Request.Query["pwd"].ToString();
        var verify = await shares.VerifyAsync(sid, string.IsNullOrEmpty(pwd) ? null : pwd, ctx.RequestAborted);
        if (verify.Code != RespCodes.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Results.Json(verify, statusCode: StatusCodes.Status403Forbidden);
        }
        var share = verify.Data!;

        // 解析实际存储路径：分享根路径 + URL 中的相对路径。
        var relPath = ctx.GetRouteValue("path")?.ToString() ?? string.Empty;
        var actualPath = ResolveActualPath(share, relPath);
        if (actualPath is null)
            return Results.NotFound("path not in share");

        return await FileDownloadHelper.StreamFileAsync(mount, actualPath, ctx);
    }

    /// <summary>
    /// 将分享相对 URL 路径映射为真实文件系统路径。单文件/单目录分享（常见的 P1 场景）
    /// 即 <c>share.Path + relPath</c>；多文件分享则需用 Files 列表匹配 relPath（P2）。
    /// </summary>
    private static string? ResolveActualPath(OpenList.Core.Models.Sharing share, string relPath)
    {
        // 统一为 '/' 分隔并去掉前导 '/'，与分享根路径做拼接。
        relPath = relPath.Replace('\\', '/').TrimStart('/');
        var root = share.Path.TrimEnd('/');

        // 空相对路径 → 分享根自身（单文件分享场景）。
        if (string.IsNullOrEmpty(relPath))
            return share.Path;

        // 单文件/单目录分享：拼接 root + relPath。
        return $"{root}/{relPath}";
    }
}
