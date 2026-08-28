using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 分片（Multipart）上传端点。对应 Go 的 <c>server/handles/multipart.go</c>
/// 与前端客户端（src/pages/home/uploads/multipart.ts）。
/// 统一使用 <c>Resp</c> 封装返回（HTTP 200，错误码在 body.code）：
/// 404=会话已回收、429=上传窗口已满（流控）、409=冲突。
/// 会话元数据经请求头（File-Path / X-File-Size / X-Chunk-Size 等）传递。
/// </summary>
public static class MultipartEndpoints
{
    /// <summary>
    /// 注册 <c>/api/fs/multipart/*</c> 分片上传路由，需登录鉴权。
    /// init(POST) 建会话、chunk(PUT) 传分片、status(GET) 查进度、
    /// complete(POST) 落盘合并、abort(POST) 丢弃会话。
    /// </summary>
    public static IEndpointRouteBuilder MapMultipartApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/fs/multipart").WithTags("Multipart").RequireAuthorization();

        // POST /api/fs/multipart/init — 初始化上传会话（幂等；相同 path+size 可续传）。

        // 头部：File-Path、X-File-Size、X-Chunk-Size，可选 X-File-Md5/Sha1/Sha256
        g.MapPost("/init", async (HttpContext ctx, IMultipartUploadService svc, ClaimsPrincipal user) =>
        {
            var path = ReadHeader(ctx, "File-Path");
            if (string.IsNullOrWhiteSpace(path))
                return Results.Ok(Resp<MultipartInitResp>.Fail("File-Path header is required", RespCodes.BadRequest));
            var size = ReadLongHeader(ctx, "X-File-Size") ?? 0;
            var chunkSize = ReadLongHeader(ctx, "X-Chunk-Size") ?? 0;
            // 组装初始化请求：对 File-Path 做 URL 解码得到目标虚拟路径
            var req = new MultipartInitRequest
            {
                Path = Uri.UnescapeDataString(path),
                Size = size,
                ChunkSize = chunkSize,
                Md5 = ReadHeader(ctx, "X-File-Md5"),
                Sha1 = ReadHeader(ctx, "X-File-Sha1"),
                Sha256 = ReadHeader(ctx, "X-File-Sha256"),
            };
            return Results.Ok(await svc.InitAsync(req, user.ToUser(), ctx.RequestAborted));
        });

        // PUT /api/fs/multipart/chunk — 上传单个分片（X-Upload-Id + X-Chunk-Index，body 为分片字节）
        g.MapPut("/chunk", async (HttpContext ctx, IMultipartUploadService svc) =>
        {
            var uploadId = ReadHeader(ctx, "X-Upload-Id");
            if (string.IsNullOrWhiteSpace(uploadId))
                return Results.Ok(Resp<MultipartSnapshot>.Fail("X-Upload-Id header is required", RespCodes.BadRequest));
            var idx = ReadIntHeader(ctx, "X-Chunk-Index");
            if (idx is null)
                return Results.Ok(Resp<MultipartSnapshot>.Fail("X-Chunk-Index header is required", RespCodes.BadRequest));
            await using var body = ctx.Request.Body;
            return Results.Ok(await svc.UploadChunkAsync(uploadId, idx.Value, body, ctx.RequestAborted));
        });

        // GET /api/fs/multipart/status?upload_id= — session snapshot (404 once reaped).
        g.MapGet("/status", async (HttpContext ctx, IMultipartUploadService svc) =>
        {
            var uploadId = ctx.Request.Query["upload_id"].ToString();
            if (string.IsNullOrWhiteSpace(uploadId))
                return Results.Ok(Resp<MultipartSnapshot>.Fail("upload_id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.StatusAsync(uploadId, ctx.RequestAborted));
        });

        // POST /api/fs/multipart/complete — header: X-Upload-Id. Flushes chunks to storage.
        g.MapPost("/complete", async (HttpContext ctx, IMultipartUploadService svc, ClaimsPrincipal user) =>
        {
            var uploadId = ReadHeader(ctx, "X-Upload-Id");
            if (string.IsNullOrWhiteSpace(uploadId))
                return Results.Ok(Resp<MultipartSnapshot>.Fail("X-Upload-Id header is required", RespCodes.BadRequest));
            return Results.Ok(await svc.CompleteAsync(uploadId, user.ToUser(), ctx.RequestAborted));
        });

        // POST /api/fs/multipart/abort — header: X-Upload-Id. Discards the session.
        g.MapPost("/abort", async (HttpContext ctx, IMultipartUploadService svc) =>
        {
            var uploadId = ReadHeader(ctx, "X-Upload-Id");
            if (string.IsNullOrWhiteSpace(uploadId))
                return Results.Ok(Resp.Fail("X-Upload-Id header is required", RespCodes.BadRequest));
            return Results.Ok(await svc.AbortAsync(uploadId, ctx.RequestAborted));
        });

        return app;
    }

    /// <summary>读取指定请求头为字符串，缺省返回 null。</summary>
    private static string? ReadHeader(HttpContext ctx, string name) =>
        ctx.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null;

    /// <summary>读取请求头并解析为 long，失败返回 null。</summary>
    private static long? ReadLongHeader(HttpContext ctx, string name) =>
        long.TryParse(ctx.Request.Headers[name].ToString(), out var v) ? v : null;

    /// <summary>读取请求头并解析为 int，失败返回 null。</summary>
    private static int? ReadIntHeader(HttpContext ctx, string name) =>
        int.TryParse(ctx.Request.Headers[name].ToString(), out var v) ? v : null;
}
