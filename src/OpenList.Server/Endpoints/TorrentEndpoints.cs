using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;

namespace OpenList.Server.Endpoints;

/// <summary>
/// torrent 相关端点（与 Go <c>server/handles/torrent.go</c> 字段对齐）：
/// <list type="bullet">
/// <item><c>POST /api/fs/torrent/parse</c> —— 解析 base64 torrent；</item>
/// <item><c>POST /api/fs/torrent/rapid_upload</c> —— 从种子提取 CAS 信息尝试秒传（需驱动支持）；</item>
/// <item><c>POST /api/fs/torrent/upload_parse</c> —— 上传 .torrent 文件并解析（multipart "torrent" 字段）。</item>
/// </list>
/// 其余 <c>/generate</c> 与旧 <c>/list|info|add|cancel</c> 暂未实现，保留 stub。
/// </summary>
public static class TorrentEndpoints
{
    /// <summary>
    /// 注册 <c>/api/fs/torrent</c> 路由组。所有端点均需登录。
    /// </summary>
    public static IEndpointRouteBuilder MapTorrentApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/fs/torrent").WithTags("Torrent").RequireAuthorization();

        g.MapPost("/parse", async (HttpContext ctx, ITorrentService torrent) =>
        {
            var req = await ReadParseReqAsync(ctx);
            if (string.IsNullOrEmpty(req)) return Results.Ok(Resp.Fail("torrent_data is required", RespCodes.BadRequest));
            return Results.Ok(await torrent.ParseAsync(req));
        });

        g.MapPost("/rapid_upload", async (HttpContext ctx, ITorrentService torrent, ClaimsPrincipal user) =>
        {
            var req = await ReadRapidUploadReqAsync(ctx);
            if (req is null)
                return Results.Ok(Resp<TorrentRapidUploadResp>.Fail("torrent_data and path are required", RespCodes.BadRequest));
            return Results.Ok(await torrent.RapidUploadAsync(req.TorrentData, req.Path, user.ToUser(), ctx.RequestAborted));
        });

        g.MapPost("/upload_parse", async (HttpContext ctx, ITorrentService torrent) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.Ok(Resp.Fail("multipart form is required", RespCodes.BadRequest));
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("torrent");
            if (file is null || file.Length == 0)
                return Results.Ok(Resp.Fail("torrent file is required", RespCodes.BadRequest));
            await using var stream = file.OpenReadStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ctx.RequestAborted);
            return Results.Ok(await torrent.UploadParseAsync(ms.ToArray(), ctx.RequestAborted));
        });

        // 未实现的端点保留 stub（与原始文件一致），便于前端正确降级。
        g.MapPost("/generate", () => Results.Ok(Resp.Fail("torrent generate not implemented", RespCodes.NotImplemented)));

        return app;
    }

    /// <summary>读取 <c>{ torrent_data }</c>，允许 form、json body 或纯文本 body。</summary>
    private static async Task<string> ReadParseReqAsync(HttpContext ctx)
    {
        // 表单
        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var fromForm = form["torrent_data"].ToString();
            if (!string.IsNullOrEmpty(fromForm)) return fromForm;
        }
        // JSON body
        try
        {
            var dto = await ctx.Request.ReadFromJsonAsync<ParseReq>(ctx.RequestAborted);
            if (dto is { TorrentData: { Length: > 0 } td }) return td;
        }
        catch { /* 退化为字符串 */ }
        // 退化：纯文本（去引号）
        try
        {
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (string.IsNullOrEmpty(body)) return string.Empty;
            return body.Trim().Trim('"');
        }
        catch { return string.Empty; }
    }

    /// <summary><c>/fs/torrent/parse</c> 的请求体。</summary>
    public sealed class ParseReq
    {
        [JsonPropertyName("torrent_data")]
        public string TorrentData { get; set; } = string.Empty;
    }

    /// <summary>读取 <c>{ torrent_data, path }</c>。</summary>
    private static async Task<RapidUploadReq?> ReadRapidUploadReqAsync(HttpContext ctx)
    {
        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var td = form["torrent_data"].ToString();
            var p = form["path"].ToString();
            if (string.IsNullOrEmpty(td) || string.IsNullOrEmpty(p)) return null;
            return new RapidUploadReq { TorrentData = td, Path = p };
        }
        // JSON body 走最小 binder（与 RequestBinder.BodyOrQueryAsync 类似，但只为该端点）
        try
        {
            var dto = await ctx.Request.ReadFromJsonAsync<RapidUploadReq>(ctx.RequestAborted);
            return dto is { TorrentData: { Length: > 0 }, Path: { Length: > 0 } } ? dto : null;
        }
        catch { return null; }
    }

    /// <summary><c>/fs/torrent/rapid_upload</c> 的请求体。</summary>
    public sealed class RapidUploadReq
    {
        /// <summary>Base64 编码的 .torrent 内容。</summary>
        [JsonPropertyName("torrent_data")]
        public string TorrentData { get; set; } = string.Empty;
        /// <summary>目标路径（虚拟路径）。</summary>
        public string Path { get; set; } = string.Empty;
    }
}