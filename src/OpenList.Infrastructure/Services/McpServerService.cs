using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 内存型 MCP 服务（Streamable HTTP）。实现 JSON-RPC 2.0 会话管理以及三个 OpenList
/// 文件系统工具，底层委托给 <see cref="IFileSystemService"/>。对应 Go 参考实现
/// <c>server/mcp</c>。
/// </summary>
public sealed class McpServerService : IMcpServerService
{
    /// <summary>当前支持的 MCP 协议版本。</summary>
    public const string ProtocolVersion = "2025-11-25";
    private static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18"];

    private const int SessionTtlMinutes = 30;   // 会话闲置 30 分钟后过期
    private const int MaxSessions = 128;        // 全局会话上限
    private const int MaxUserSessions = 16;     // 单用户会话上限

    private readonly IFileSystemService _fs;
    private readonly AppConfiguration _config;
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new();

    /// <summary>
    /// 创建 MCP 服务实例。
    /// </summary>
    /// <param name="fs">文件系统服务，供 MCP 工具实际执行操作。</param>
    /// <param name="config">应用配置，用于读取 MCP 开关。</param>
    public McpServerService(IFileSystemService fs, AppConfiguration config)
    {
        _fs = fs;
        _config = config;
    }

    /// <summary>MCP 是否启用（取配置中的 <c>MCP.Enable</c>）。</summary>
    public bool Enabled => _config.MCP.Enable;

    /// <summary>
    /// 协商协议版本：客户端请求的版本受支持则用其返回，否则回退到默认版本。
    /// </summary>
    public string NegotiateProtocolVersion(string? requested)
        => requested is not null && SupportedVersions.Contains(requested) ? requested : ProtocolVersion;

    /// <summary>
    /// 初始化 MCP 会话：复用属于该用户的既有会话，或创建新会话（受单用户/全局上限约束）。
    /// </summary>
    /// <param name="userId">发起请求的用户主键。</param>
    /// <param name="requestedSessionId">客户端请求复用的会话 id（可选）。</param>
    /// <param name="protocolVersion">协商后的协议版本。</param>
    /// <returns>当前会话 id。</returns>
    public string InitializeSession(long userId, string? requestedSessionId, string protocolVersion)
    {
        lock (_sessions)
        {
            PruneExpiredLocked(DateTime.UtcNow);

            if (!string.IsNullOrEmpty(requestedSessionId) &&
                _sessions.TryGetValue(requestedSessionId, out var existing) &&
                existing.UserId == userId)
            {
                existing.Initialized = false;
                existing.ProtocolVersion = protocolVersion;
                existing.LastUsedAt = DateTime.UtcNow;
                return existing.Id;
            }

            // 单用户上限：淘汰该用户最久未使用的会话。
            var userSessions = _sessions.Values.Where(s => s.UserId == userId)
                .OrderBy(s => s.LastUsedAt).ToList();
            foreach (var s in userSessions.Take(Math.Max(0, userSessions.Count - MaxUserSessions + 1)))
                _sessions.TryRemove(s.Id, out _);

            // 全局上限：淘汰最久未使用的会话。
            var all = _sessions.Values.OrderBy(s => s.LastUsedAt).ToList();
            foreach (var s in all.Take(Math.Max(0, all.Count - MaxSessions + 1)))
                _sessions.TryRemove(s.Id, out _);

            var session = new McpSession
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                ProtocolVersion = protocolVersion,
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow,
            };
            _sessions[session.Id] = session;
            return session.Id;
        }
    }

    /// <summary>
    /// 校验会话是否有效（存在、属于该用户、未过期）。有效时刷新其最近使用时间。
    /// </summary>
    public bool ValidateSession(string sessionId, long userId)
    {
        lock (_sessions)
        {
            if (!TryGetLiveLocked(sessionId, userId, out var s)) return false;
            s.LastUsedAt = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// 将会话标记为已初始化（客户端完成 initialize 握手）。
    /// </summary>
    public bool MarkInitialized(string sessionId)
    {
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || SessionExpired(s, DateTime.UtcNow))
            {
                if (s is not null) _sessions.TryRemove(sessionId, out _);
                return false;
            }
            s.Initialized = true;
            s.LastUsedAt = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>判断会话是否已初始化。</summary>
    public bool IsInitialized(string sessionId, long userId)
    {
        lock (_sessions)
        {
            if (!TryGetLiveLocked(sessionId, userId, out var s)) return false;
            return s.Initialized;
        }
    }

    /// <summary>删除指定会话（仅限属于该用户的会话）。</summary>
    public bool DeleteSession(string sessionId, long userId)
    {
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.UserId != userId) return false;
            return _sessions.TryRemove(sessionId, out _);
        }
    }

    /// <summary>
    /// 列出所有 MCP 工具的定义（openlist.fs 的 list/get/link）。
    /// </summary>
    public JsonArray ListTools()
    {
        var tools = new JsonArray
        {
            Tool("openlist.fs.list", "OpenList FS List",
                "List files and directories under a mount path that the current user can access.",
                [("path", "string", "Mount path to list, for example \"/\" or \"/movies\".", true),
                 ("refresh", "boolean", "Refresh the directory listing before returning results.", false),
                 ("password", "string", "Optional password for protected paths.", false),
                 ("page", "integer", "1-based page number.", false),
                 ("per_page", "integer", "Page size.", false)]),
            Tool("openlist.fs.get", "OpenList FS Get",
                "Get file or directory details for a mount path that the current user can access.",
                [("path", "string", "Mount path to inspect, for example \"/movies/demo.mp4\".", true),
                 ("password", "string", "Optional password for protected paths.", false)]),
            Tool("openlist.fs.link", "OpenList FS Link",
                "Return usable link information for a file path that the current user can access.",
                [("path", "string", "File mount path, for example \"/movies/demo.mp4\".", true),
                 ("password", "string", "Optional password for protected paths.", false),
                 ("type", "string", "Optional link type forwarded to storage drivers.", false)]),
        };
        return tools;
    }

    /// <summary>
    /// 调用指定 MCP 工具，把结果序列化为文本内容返回（JSON-RPC 工具响应格式）。
    /// </summary>
    /// <param name="name">工具名（openlist.fs.*）。</param>
    /// <param name="arguments">工具入参 JSON。</param>
    /// <param name="userId">调用者用户主键（以管理员身份执行）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>MCP 工具调用结果对象。</returns>
    public async Task<JsonObject> CallToolAsync(string name, JsonElement arguments, long userId, CancellationToken ct = default)
    {
        var user = new User { Id = userId, Role = UserRole.Admin };
        var texts = new List<string>();
        try
        {
            switch (name)
            {
                case "openlist.fs.list":
                {
                    var req = new FsListReq
                    {
                        Path = GetString(arguments, "path", "/") ?? "/",
                        Password = GetString(arguments, "password", null),
                        Refresh = GetBool(arguments, "refresh", false),
                        Page = GetInt(arguments, "page", 1),
                        PerPage = GetInt(arguments, "per_page", 30),
                    };
                    var resp = await _fs.ListAsync(req, user, ct);
                    texts.Add(JsonSerializer.Serialize(resp.Data));
                    if (resp.Code != 200) texts.Add(resp.Message);
                    break;
                }
                case "openlist.fs.get":
                {
                    var req = new FsGetReq
                    {
                        Path = GetString(arguments, "path", "/") ?? "/",
                        Password = GetString(arguments, "password", null),
                    };
                    var resp = await _fs.GetAsync(req, user, ct);
                    texts.Add(JsonSerializer.Serialize(resp.Data));
                    if (resp.Code != 200) texts.Add(resp.Message);
                    break;
                }
                case "openlist.fs.link":
                {
                    var resp = await _fs.LinkAsync(GetString(arguments, "path", "/") ?? "/", user, ct);
                    texts.Add(JsonSerializer.Serialize(resp.Data));
                    if (resp.Code != 200) texts.Add(resp.Message);
                    break;
                }
                default:
                    return new JsonObject
                    {
                        ["isError"] = true,
                        ["content"] = new JsonArray(TextContent($"unknown tool: {name}")),
                    };
            }
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray(TextContent($"error: {ex.Message}")),
            };
        }

        var content = new JsonArray(texts.Select(TextContent).ToArray());
        return new JsonObject { ["content"] = content };
    }

    // --- 辅助方法 ---

    /// <summary>构造 MCP 文本内容对象。</summary>
    private static JsonObject TextContent(string text) => new()
    {
        ["type"] = "text",
        ["text"] = text,
    };

    /// <summary>构造 MCP 工具定义对象（含输入 JSON Schema）。</summary>
    private static JsonObject Tool(string name, string title, string description, params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var (pName, pType, pDesc, _) in props)
        {
            properties[pName] = new JsonObject
            {
                ["type"] = pType,
                ["description"] = pDesc,
            };
        }
        var required = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray());
        return new JsonObject
        {
            ["name"] = name,
            ["title"] = title,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
            },
        };
    }

    /// <summary>从参数 JSON 中读取字符串（数字也转成其字面文本），缺省返回默认值。</summary>
    private static string? GetString(JsonElement args, string key, string? def)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var el))
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
        }
        return def;
    }

    /// <summary>从参数 JSON 中读取布尔值，缺省返回默认值。</summary>
    private static bool GetBool(JsonElement args, string key, bool def)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var el) &&
            el.ValueKind == JsonValueKind.True) return true;
        return def;
    }

    /// <summary>从参数 JSON 中读取整数，缺省返回默认值。</summary>
    private static int GetInt(JsonElement args, string key, int def)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var el) &&
            el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return v;
        return def;
    }

    /// <summary>加锁状态下查找存活的会话（属于该用户且未过期），过期则顺手删除。</summary>
    private bool TryGetLiveLocked(string sessionId, long userId, out McpSession session)
    {
        if (_sessions.TryGetValue(sessionId, out var s) && s.UserId == userId && !SessionExpired(s, DateTime.UtcNow))
        {
            session = s;
            return true;
        }
        if (s is not null) _sessions.TryRemove(sessionId, out _);
        session = null!;
        return false;
    }

    /// <summary>判断会话是否已超过闲置过期时间。</summary>
    private static bool SessionExpired(McpSession s, DateTime now)
        => now - s.LastUsedAt > TimeSpan.FromMinutes(SessionTtlMinutes);

    /// <summary>加锁状态下清理所有已过期会话。</summary>
    private void PruneExpiredLocked(DateTime now)
    {
        foreach (var s in _sessions.Values.Where(x => SessionExpired(x, now)))
            _sessions.TryRemove(s.Id, out _);
    }

    /// <summary>单个 MCP 会话的状态。</summary>
    private sealed class McpSession
    {
        public string Id { get; init; } = "";
        public long UserId { get; init; }
        public string ProtocolVersion { get; set; } = "2025-11-25";
        public bool Initialized { get; set; }
        public DateTime CreatedAt { get; init; }
        public DateTime LastUsedAt { get; set; }
    }
}