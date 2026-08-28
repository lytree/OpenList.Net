using System.Text.Json.Nodes;

namespace OpenList.Core.Interfaces;

/// <summary>
/// MCP（Model Context Protocol）服务器——Streamable HTTP 传输，JSON-RPC 2.0。
/// 与 OpenList Go 的 <c>server/mcp</c> 对应。会话管理在内存中完成
/// （30 分钟 TTL，全局 128 / 每用户 16）；<see cref="Enabled"/> 控制整个端点的开关
/// （未启用时返回 403）。
/// </summary>
public interface IMcpServerService
{
    /// <summary>MCP 服务器是否启用（配置项 <c>MCP.Enable</c>）。</summary>
    bool Enabled { get; }

    /// <summary>协议协商：回显一个受支持的版本，否则返回默认版本。</summary>
    string NegotiateProtocolVersion(string? requested);

    /// <summary>在 <c>initialize</c> 期间创建（或复用）会话，返回其 id。</summary>
    string InitializeSession(long userId, string? requestedSessionId, string protocolVersion);

    /// <summary>会话存在、属于该用户且未过期（会刷新 lastUsedAt 时间戳）。</summary>
    bool ValidateSession(string sessionId, long userId);

    /// <summary>将会话标记为已初始化（收到 <c>notifications/initialized</c>）。</summary>
    bool MarkInitialized(string sessionId);

    /// <summary>会话存在、属于该用户、未过期且已初始化。</summary>
    bool IsInitialized(string sessionId, long userId);

    /// <summary>删除一个会话（DELETE /mcp）。</summary>
    bool DeleteSession(string sessionId, long userId);

    /// <summary>tools/list 的载荷（工具定义的数组）。</summary>
    JsonArray ListTools();

    /// <summary>执行一个工具，返回 tools/call 的结果对象（含 content 数组）。</summary>
    Task<JsonObject> CallToolAsync(string name, System.Text.Json.JsonElement arguments, long userId, CancellationToken ct = default);
}
