using System.Text.Json;
using OpenList.Core.Models;

namespace OpenList.Server.Middleware;

/// <summary>
/// 全局异常处理中间件，镜像 Go 的 <c>gin.RecoveryWithWriter</c>：捕获管道内未处理异常，
/// 记录错误日志并返回统一 <c>Resp</c> JSON（不向客户端泄露堆栈）。已在响应开始后抛出的
/// 异常无法改写响应，原样向上抛。请求体解析失败（BadHttpRequestException）映射为 400。
/// </summary>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (BadHttpRequestException ex)
        {
            _logger.LogWarning(ex, "Bad request to {Path}", ctx.Request.Path);
            if (ctx.Response.HasStarted) throw;
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteAsync(ctx, Resp.Fail("invalid request body", RespCodes.BadRequest));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while processing {Method} {Path}",
                ctx.Request.Method, ctx.Request.Path);
            if (ctx.Response.HasStarted) throw;
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteAsync(ctx, Resp.Fail("internal server error", RespCodes.InternalError));
        }
    }

    private static async Task WriteAsync(HttpContext ctx, Resp body)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(body, JsonOptions, ctx.RequestAborted);
    }
}
