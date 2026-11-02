using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Infrastructure.Services;
using OpenList.Server.Middleware;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 分享签名下载端点：/sd/{sid}[/{**path}] 与 /sad/{sid}[/{**path}]。
/// 对应 OpenList Go <c>server/handles/sharing.go</c> 的 DownShare 与 SharingArchiveExtract。
/// 公开（无需 JWT），仅凭分享码（sid）访问，且受可选密码（?pwd=）、
/// 过期时间、禁用标记与最大访问次数（max-accessed）约束。
/// GET 与 HEAD 均受支持。
/// </summary>
/// <remarks>
/// <b>分享是只读能力。</b> 此前 <c>/sad</c> 会把分享内的归档解压到分享根所在目录 ——
/// 任何一个拿到分享链接的匿名访客都能在别人的存储里写文件，甚至可以借归档内的路径穿越
/// 覆盖分享范围之外的内容。这里改成明确的只读拒绝：解压属于写操作，必须由登录用户在
/// <c>/api/fs/archive/decompress</c> 上完成（该端点带权限位与 meta 双重门禁）。
/// HEAD 仍然只做大小探测，不产生任何副作用。
/// </remarks>
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

        // GET /sad/{sid}[/{**path}]?pwd=... — 历史遗留的「分享内归档解压」入口。
        // 保留路由是为了让旧客户端拿到可读的 403 而不是 404，但**不再执行解压**：
        // 匿名访客不应在分享根所在目录写入任何文件。
        // HEAD 仍走 /sd 的流式处理器（只探测归档大小，无副作用）。
        app.MapGet("/sad/{sid}/{**path}", HandleArchiveAsync).AllowAnonymous();
        app.MapGet("/sad/{sid}", HandleArchiveAsync).AllowAnonymous();
        app.MapMethods("/sad/{sid}/{**path}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();
        app.MapMethods("/sad/{sid}", new[] { "HEAD" }, HandleAsync).AllowAnonymous();

        // GET/HEAD /sae/{sid}[/{**path}]?inner=...&pwd=...&pass=...
        // 分享内归档的**只读单条目预览**：只调用 OpenArchiveEntryAsync，绝不落盘，
        // 让公开分享页也能查看归档里的图片/文本/音视频，同时保持「分享只读」语义。
        app.MapGet("/sae/{sid}/{**path}", HandleArchiveEntryAsync).AllowAnonymous();
        app.MapGet("/sae/{sid}", HandleArchiveEntryAsync).AllowAnonymous();
        app.MapMethods("/sae/{sid}/{**path}", new[] { "HEAD" }, HandleArchiveEntryAsync).AllowAnonymous();
        app.MapMethods("/sae/{sid}", new[] { "HEAD" }, HandleArchiveEntryAsync).AllowAnonymous();
        return app;
    }

    /// <summary>公开分享的归档条目读取使用的访客用户（仅供读取，不含任何权限位）。</summary>
    private static readonly User GuestUser = new() { Id = 0, Username = "", Role = UserRole.Guest };

    /// <summary>公开分享归档条目的单次读取上限（防止 ZIP 解压放大把服务器磁盘/带宽吃满）。</summary>
    private const long MaxSharedEntryBytes = 512L * 1024 * 1024;

    /// <summary>归档条目扩展名 → MIME（浏览器要能直接渲染图片/视频/PDF）。</summary>
    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// 公开分享的归档条目预览：校验分享与路径后，把归档内的单个条目以流式返回（支持 Range）。
    /// <para>
    /// 与 <c>/ae</c> 的区别：<c>/ae</c> 需要登录态签名，游客拿不到；这里以分享码作为
    /// 能力凭证。两条路径都只读 —— 绝不会触发解压写盘。
    /// </para>
    /// </summary>
    private static async Task<IResult> HandleArchiveEntryAsync(
        HttpContext ctx,
        ISharingService shares,
        IFileSystemService fs,
        RateLimitManager limits,
        ShareStreamBudget budget)
    {
        var sid = ctx.GetRouteValue("sid")?.ToString();
        if (string.IsNullOrEmpty(sid)) return Results.NotFound();

        var pwd = ctx.Request.Query["pwd"].ToString();
        var verify = await shares.VerifyAsync(sid, string.IsNullOrEmpty(pwd) ? null : pwd, ctx.RequestAborted);
        if (verify.Code != RespCodes.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Results.Json(verify, statusCode: StatusCodes.Status403Forbidden);
        }
        var share = verify.Data!;

        var relPath = ctx.GetRouteValue("path")?.ToString() ?? string.Empty;
        var archivePath = ResolveActualPath(share, relPath);
        if (archivePath is null) return Results.NotFound("path not in share");

        // 条目路径：必须是归档内的相对路径。绝对路径、`.`/`..` 段、目录结尾一律拒绝 ——
        // 归档读取器会把这些交给底层库解析，越界条目可能读到归档之外的内容。
        var inner = ctx.Request.Query["inner"].ToString().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(inner) || inner.EndsWith('/'))
            return Results.BadRequest("inner is required");

        var segments = inner.TrimStart('/').Split('/');
        if (segments.Length == 0 || segments.Any(s => s is "" or "." or ".."))
            return Results.BadRequest("invalid inner path");

        var pass = ctx.Request.Query["pass"].ToString();
        var resp = await fs.OpenArchiveEntryAsync(
            archivePath, inner, string.IsNullOrEmpty(pass) ? null : pass, GuestUser, ctx.RequestAborted);
        if (resp.Code != RespCodes.Ok || resp.Data is null)
            return Results.NotFound(resp.Message);

        var stream = resp.Data;
        // 预算护栏：能拿到长度时先判上限，避免单个条目把带宽/内存打满。
        if (stream.CanSeek && stream.Length > MaxSharedEntryBytes)
        {
            await stream.DisposeAsync();
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // 并发预算：放在所有校验之后申请 —— 前面任何一条 400/404 早退都不能吃掉名额。
        // HEAD 只探大小，不占额度；GET 超限回 429 让客户端退避重试。
        var lease = await AcquireOrNullAsync(budget, sid, ctx);
        if (lease is null)
        {
            await stream.DisposeAsync();
            return TooManyStreams(ctx);
        }

        var name = segments[^1];
        var contentType = ContentTypes.TryGetContentType(name, out var mapped)
            ? mapped
            : "application/octet-stream";
        await limits.EnsureFreshAsync(ctx.RequestAborted);
        // 条目字节由框架直接拷给客户端：只能从读端施加服务端下载限速。
        // 租约挂在「响应完成」上而不是流的 Dispose —— FileResult 是否释放流属于框架实现细节，
        // 押在它上面一旦不释放就会永久漏算额度。
        ReleaseOnResponseEnd(ctx, lease!);
        var limited = TrafficLimit.LimitRead(stream, limits.ServerDownload);
        return Results.File(limited, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// 在响应结束时归还并发额度（正常写完、客户端断开、异常都会触发）。
    /// </summary>
    private static void ReleaseOnResponseEnd(HttpContext ctx, IDisposable lease)
        => ctx.Response.OnCompleted(() =>
        {
            lease.Dispose();
            return Task.CompletedTask;
        });

    /// <summary>
    /// 为公开分享流申请并发额度。HEAD 请求不占额度（只探测大小，不占带宽）；
    /// 超限返回 <c>null</c>。
    /// </summary>
    private static async Task<IDisposable?> AcquireOrNullAsync(ShareStreamBudget budget, string sid, HttpContext ctx)
    {
        if (HttpMethods.IsHead(ctx.Request.Method)) return NoLease.Instance;
        return await budget.TryAcquireAsync(sid, ctx.RequestAborted);
    }

    /// <summary>并发超限：429 + Retry-After，播放器/下载器会自动退避。</summary>
    private static IResult TooManyStreams(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = "1";
        return Results.Json(
            Resp.Fail("too many concurrent streams for this share", RespCodes.TooManyRequests),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>不占额度时的占位租约（HEAD 等无副作用请求）。</summary>
    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 处理历史 <c>/sad</c> 解压入口：先按 <c>/sd</c> 的规则校验分享与路径
    /// （越界路径仍然 404，不泄露分享结构），再返回 403 说明分享为只读。
    /// </summary>
    private static async Task<IResult> HandleArchiveAsync(
        HttpContext ctx,
        ISharingService shares)
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

        // 路径解析与 /sd 完全一致：先确认请求落在分享范围内，否则 404。
        var relPath = ctx.GetRouteValue("path")?.ToString() ?? string.Empty;
        if (ResolveActualPath(share, relPath) is null)
            return Results.NotFound("path not in share");

        // 分享是只读的：解压会写盘，必须走登录态的 /api/fs/archive/decompress。
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Results.Json(
            Resp.Fail("share is read-only: archive extraction requires an authenticated session", RespCodes.Forbidden),
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// 处理 /sd 文件流式下载：校验分享后解析实际路径，委托
    /// <see cref="FileDownloadHelper"/> 流式输出（支持 Range）。鉴权失败返回 403。
    /// </summary>
    private static async Task<IResult> HandleAsync(
        HttpContext ctx,
        ISharingService shares,
        MountService mount,
        RateLimitManager limits,
        ShareStreamBudget budget)
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

        // 并发预算：同一分享码同时在传的流数超过设置上限即 429。
        var lease = await AcquireOrNullAsync(budget, sid, ctx);
        if (lease is null) return TooManyStreams(ctx);

        // 流式输出在本方法内完成，但响应可能还在 flush：挂在完成回调上更准确。
        ReleaseOnResponseEnd(ctx, lease);
        return await FileDownloadHelper.StreamFileAsync(mount, actualPath, ctx, limits);
    }

    /// <summary>
    /// 将分享相对 URL 路径映射为真实文件系统路径，并做规范化 + 包含关系检查。
    /// <para>
    /// 旧实现用字符串直接拼 <c>share.Path + "/" + relPath</c>，`..` 段会原样进入 VFS，
    /// 从而越出分享根。这里改为：逐段拒绝 <c>.</c> / <c>..</c>，规范化后再用
    /// <see cref="MetaAccess.IsSubPath"/> 确认结果仍落在分享路径内。
    /// </para>
    /// 多文件分享需要按 Files 列表匹配 relPath 的能力（P2），当前仍按单根分享处理。
    /// </summary>
    private static string? ResolveActualPath(Sharing share, string relPath)
    {
        var root = MetaAccess.Normalize(share.Path);

        // 空相对路径 → 分享根自身（单文件分享场景）。
        if (string.IsNullOrWhiteSpace(relPath))
            return root;

        var rel = relPath.Replace('\\', '/').TrimStart('/');
        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // `..` 一律拒绝而不是交给规范化「吃掉」：规范化的结果无法区分「越界后又被拉回」，
        // 直接拒绝才能保证 URL 语义与最终路径一一对应。
        if (segments.Length == 0 || segments.Any(s => s == "." || s == ".."))
            return null;

        var candidate = MetaAccess.Normalize(root == "/" ? "/" + string.Join('/', segments) : root + "/" + string.Join('/', segments));
        return MetaAccess.IsSubPath(root, candidate) ? candidate : null;
    }
}
