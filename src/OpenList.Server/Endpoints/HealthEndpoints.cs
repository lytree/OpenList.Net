using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Health check endpoints. <c>GET /ping</c> → "pong".
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ping", () => "pong")
           .WithDescription("Health check")
           .AllowAnonymous();
        return app;
    }
}
