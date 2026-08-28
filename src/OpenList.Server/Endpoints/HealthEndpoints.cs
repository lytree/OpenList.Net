using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 健康检查端点。<c>GET /ping</c> → "pong"。
/// 用于探活（负载均衡常以 GET/HEAD 探测），无需鉴权。
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// 注册健康检查路由。<c>GET|HEAD|POST /ping</c> 返回固定字符串 "pong"，匿名可访问。
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Go registers Any /ping — accept GET/HEAD/POST (some load balancers probe HEAD).
        app.MapMethods("/ping", ["GET", "HEAD", "POST"], () => "pong")
           .WithDescription("Health check")
           .AllowAnonymous();
        return app;
    }
}
