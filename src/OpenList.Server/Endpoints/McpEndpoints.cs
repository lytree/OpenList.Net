using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// MCP（Model Context Protocol）端点——采用 Streamable HTTP 传输、JSON-RPC 2.0。
/// 对应 Go <c>server/mcp.go</c> 与 <c>server/mcp/handler.go</c>。
/// 仅管理员可访问（RequireAuthorization("admin")）。
/// 提供 initialize、notifications/initialized、ping、tools/list、tools/call 等方法，
/// 内置 openlist.fs.list / openlist.fs.get / openlist.fs.link 三个工具。
/// </summary>
public static class McpEndpoints
{
    private const string ProtocolVersionHeader = "MCP-Protocol-Version";
    private const string SessionHeader = "MCP-Session-Id";

    /// <summary>
    /// 注册 /mcp 路由组（仅管理员）。
    /// GET 用于探测，始终返回 405（带 Allow 头）；DELETE 结束会话；
    /// POST 处理 JSON-RPC 2.0 请求（初始化/工具调用等），并校验 MCP 会话的有效性。
    /// </summary>
    public static IEndpointRouteBuilder MapMcpApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/mcp").WithTags("Mcp").RequireAuthorization("admin");

        // GET /mcp — 探测：始终返回 405 并声明 Allow: POST, DELETE（对齐 Go 的 handleGet）。
        g.MapGet("", (HttpContext ctx, IMcpServerService svc) =>
        {
            // MCP 未启用或 Origin 校验不通过时直接 403。
            if (!svc.Enabled) return Forbidden();
            if (!ValidateOrigin(ctx.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            ctx.Response.Headers.Allow = "POST, DELETE";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });

        // DELETE /mcp — 结束由 MCP-Session-Id 标识的会话。
        g.MapDelete("", (HttpContext ctx, ClaimsPrincipal user, IMcpServerService svc) =>
        {
            if (!svc.Enabled) return Forbidden();
            // 缺少会话 id 视为不存在（404）；删除成功返回 204。
            var sessionId = ctx.Request.Headers[SessionHeader].ToString();
            if (string.IsNullOrEmpty(sessionId)) return Results.StatusCode(StatusCodes.Status404NotFound);
            return svc.DeleteSession(sessionId, user.UserId() ?? 0)
                ? Results.StatusCode(StatusCodes.Status204NoContent)
                : Results.StatusCode(StatusCodes.Status404NotFound);
        });

        // POST /mcp — JSON-RPC 2.0 处理入口。
        g.MapPost("", async (HttpContext ctx, ClaimsPrincipal user, IMcpServerService svc) =>
        {
            if (!svc.Enabled) return Forbidden();
            if (!ValidateOrigin(ctx.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            // 客户端必须同时接受 application/json 与 text/event-stream，否则 406。
            if (!AcceptsStreamableHttp(ctx.Request.Headers.Accept.ToString()))
            {
                return Results.Json(McpError(null, -32000,
                    "Not Acceptable: client must accept both application/json and text/event-stream"),
                    statusCode: StatusCodes.Status406NotAcceptable);
            }

            // 读取请求体（上限 1 MB）并解析为 JSON-RPC 请求。
            string body;
            using (var ms = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                if (ms.Length > 1024 * 1024)
                {
                    return Results.Json(McpError(null, -32000, "request body too large"),
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                body = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            JsonElement root;
            try { root = JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException)
            {
                return Results.Json(McpError(null, -32700, "parse error"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // 校验 JSON-RPC 版本号必须为 "2.0"，否则 400 无效请求。
            if (!root.TryGetProperty("jsonrpc", out var jsonrpc) || jsonrpc.GetString() != "2.0")
            {
                return Results.Json(McpError(GetId(root), -32600, "invalid request"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var id = GetId(root);
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var hasParams = root.TryGetProperty("params", out var prms);

            // initialize：协商协议版本、建立会话并下发服务能力（tools）。
            if (method == "initialize")
            {
                var requestedVersion = hasParams && prms.ValueKind == JsonValueKind.Object &&
                                       prms.TryGetProperty("protocolVersion", out var pv)
                    ? pv.GetString() : null;
                var protocolVersion = svc.NegotiateProtocolVersion(requestedVersion);
                var requestedSession = ctx.Request.Headers[SessionHeader].ToString();
                var sessionId = svc.InitializeSession(user.UserId() ?? 0, requestedSession, protocolVersion);
                ctx.Response.Headers[SessionHeader] = sessionId;
                var result = new JsonObject
                {
                    ["protocolVersion"] = protocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "OpenList MCP",
                        ["version"] = "0.0.0",
                    },
                    ["instructions"] = "Complete initialization with notifications/initialized, then use tools/list and tools/call. Available tools include openlist.fs.list, openlist.fs.get, and openlist.fs.link.",
                };
                return Results.Json(McpOk(id, result));
            }

            // 非 initialize 请求必须携带有效会话头，否则视为 400/404。
            var sessionIdHeader = ctx.Request.Headers[SessionHeader].ToString();
            if (string.IsNullOrEmpty(sessionIdHeader))
            {
                return Results.Json(McpError(id, -32000, "missing MCP session"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (!svc.ValidateSession(sessionIdHeader, user.UserId() ?? 0))
            {
                return Results.Json(McpError(id, -32001, "session not found"),
                    statusCode: StatusCodes.Status404NotFound);
            }

            // 按方法名分发：ping、初始化通知、工具列表、工具调用。
            switch (method)
            {
                case "ping":
                    return Results.Json(McpOk(id, new JsonObject()));
                case "notifications/initialized":
                    // 客户端完成初始化后上报；服务端标记会话已就绪，返回 202。
                    svc.MarkInitialized(sessionIdHeader);
                    return Results.StatusCode(StatusCodes.Status202Accepted);
                case "tools/list":
                    // 仅已初始化的会话允许列举工具。
                    if (!svc.IsInitialized(sessionIdHeader, user.UserId() ?? 0))
                        return Results.Json(McpError(id, -32002, "MCP session not initialized"),
                            statusCode: StatusCodes.Status400BadRequest);
                    return Results.Json(McpOk(id, new JsonObject { ["tools"] = svc.ListTools() }));
                case "tools/call":
                    // 仅已初始化的会话允许调用工具。
                    if (!svc.IsInitialized(sessionIdHeader, user.UserId() ?? 0))
                        return Results.Json(McpError(id, -32002, "MCP session not initialized"),
                            statusCode: StatusCodes.Status400BadRequest);
                    {
                        // 从 params 提取工具名与参数并执行。
                        var toolName = hasParams && prms.ValueKind == JsonValueKind.Object &&
                                       prms.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                        var args = hasParams && prms.ValueKind == JsonValueKind.Object &&
                                   prms.TryGetProperty("arguments", out var a) ? a : default;
                        if (toolName is null)
                            return Results.Json(McpError(id, -32602, "invalid tools/call params"),
                                statusCode: StatusCodes.Status400BadRequest);
                        var toolResult = await svc.CallToolAsync(toolName, args, user.UserId() ?? 0, ctx.RequestAborted);
                        return Results.Json(McpOk(id, toolResult));
                    }
                default:
                    return Results.Json(McpError(id, -32601, $"method \"{method}\" not implemented yet"));
            }
        });

        return app;
    }

    // --- 辅助方法 ---

    /// <summary>返回 MCP 未启用时的统一 403 响应。</summary>
    private static IResult Forbidden() => Results.Json(
        new { code = 403, message = "MCP server is not enabled" },
        statusCode: StatusCodes.Status403Forbidden);

    /// <summary>构造 JSON-RPC 2.0 成功响应（附带可选的 id）。</summary>
    private static JsonObject McpOk(JsonElement? id, JsonObject result)
    {
        var obj = new JsonObject { ["jsonrpc"] = "2.0", ["result"] = result };
        if (id.HasValue && id.Value.ValueKind != JsonValueKind.Undefined) obj["id"] = JsonNode.Parse(id.Value.GetRawText());
        return obj;
    }

    /// <summary>构造 JSON-RPC 2.0 错误响应（附带可选 id 与错误码/消息）。</summary>
    private static JsonObject McpError(JsonElement? id, int code, string message)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        if (id.HasValue && id.Value.ValueKind != JsonValueKind.Undefined) obj["id"] = JsonNode.Parse(id.Value.GetRawText());
        return obj;
    }

    /// <summary>从 JSON-RPC 请求根对象中提取 "id" 字段，用于响应对应匹配。</summary>
    private static JsonElement? GetId(JsonElement root)
        => root.TryGetProperty("id", out var id) ? id : null;

    /// <summary>
    /// 校验 Origin 与请求 Host 一致（同源），阻止跨站请求。无 Origin 头时放行。
    /// </summary>
    private static bool ValidateOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || string.IsNullOrEmpty(originUri.Host))
            return false;
        return string.Equals(originUri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断客户端 Accept 是否支持 Streamable HTTP 所要求的介质类型。</summary>
    private static bool AcceptsStreamableHttp(string? accept)
    {
        if (string.IsNullOrEmpty(accept)) return false;
        return accept.Contains("*/*", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }
}
