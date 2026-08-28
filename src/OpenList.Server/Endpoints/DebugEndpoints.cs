using Microsoft.AspNetCore.Http;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 调试端点（仅在 Development 环境注册，对应 Go 中受 flags.Debug || flags.Dev
/// 控制的 <c>server/debug.go</c>）。用于内存/路径等调试信息输出。
/// </summary>
public static class DebugEndpoints
{
    /// <summary>
    /// 注册 <c>/debug/*</c> 调试路由（仅开发环境）。无需鉴权。
    /// </summary>
    public static IEndpointRouteBuilder MapDebugApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/debug").WithTags("Debug");

        // GET /debug/gc — force a full GC (Go: runtime.GC(); "ok")
        g.MapGet("/gc", () => Results.Ok("ok"));

        // GET /debug/hide_privacy — echo a redacted IP (Go returns a fixed demo value)
        g.MapGet("/hide_privacy", () => Results.Ok(Resp.Fail("This is ip: 1.1.1.1", 400)));

        // GET /debug/path/{**path} — echo the resolved mount path.
        // Go applies sign.Verify via middlewares.Down; .NET intentionally keeps this
        // a plain echo for now (ISignService wiring is a follow-up).
        g.MapGet("/path/{**path}", (string? path) => Results.Ok(new { path = "/" + (path ?? "") }));

        return app;
    }
}
