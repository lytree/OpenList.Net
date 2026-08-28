using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 分享管理。与 OpenList Go 的 <c>internal/op/sharing.go</c> +
/// <c>server/handles/sharing.go</c> 对应。管理操作的查询使用用户可见的
/// <see cref="Sharing.ShareCode"/>（前端 string id），而不是数据库主键。
/// </summary>
public interface ISharingService
{
    /// <summary>列出分享；isAdmin 表示是否以管理员身份查看全部用户的分享。</summary>
    Task<Resp<List<ShareInfo>>> ListAsync(long userId, bool isAdmin, CancellationToken ct = default);
    /// <summary>按分享码获取单个分享。</summary>
    Task<Resp<ShareInfo>> GetAsync(string code, long userId, bool isAdmin, CancellationToken ct = default);
    /// <summary>创建分享；isAdmin 表示是否以管理员身份创建。</summary>
    Task<Resp<ShareInfo>> CreateAsync(ShareReq req, long userId, bool isAdmin, CancellationToken ct = default);
    /// <summary>更新指定分享码对应的分享。</summary>
    Task<Resp<ShareInfo>> UpdateAsync(string code, ShareReq req, long userId, bool isAdmin, CancellationToken ct = default);
    /// <summary>删除分享。</summary>
    Task<Resp> DeleteAsync(string code, long userId, bool isAdmin, CancellationToken ct = default);
    /// <summary>启用分享。</summary>
    Task<Resp> EnableAsync(string code, long userId, bool isAdmin, CancellationToken ct = default);
    /// <summary>禁用分享。</summary>
    Task<Resp> DisableAsync(string code, long userId, bool isAdmin, CancellationToken ct = default);
    /// <summary>校验分享码与密码，返回分享实体；用于公开的分享访问。</summary>
    Task<Resp<Sharing>> VerifyAsync(string code, string? password, CancellationToken ct = default);
}

/// <summary>创建/更新分享的请求 DTO——与 Go 的 UpdateSharingReq 对应。</summary>
public sealed class ShareReq
{
    /// <summary>用户可见的分享 id（Go: string id）。创建时为空由服务器生成。</summary>
    [JsonPropertyName("id")]
    public string? ShareCode { get; set; }

    /// <summary>新的分享 id（用于重命名分享码）。</summary>
    [JsonPropertyName("new_id")]
    public string? NewShareCode { get; set; }

    /// <summary>分享的文件/目录路径列表。</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>过期时间，可为空。</summary>
    [JsonPropertyName("expires")]
    public DateTimeOffset? Expiration { get; set; }

    /// <summary>访问密码，可为空。</summary>
    [JsonPropertyName("pwd")]
    public string? Password { get; set; }

    /// <summary>允许的最大访问次数。</summary>
    [JsonPropertyName("max_accessed")]
    public int MaxAccessed { get; set; }

    /// <summary>是否禁用。</summary>
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    /// <summary>备注。</summary>
    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    /// <summary>分享 readme。</summary>
    [JsonPropertyName("readme")]
    public string? Readme { get; set; }

    /// <summary>分享页自定义头部。</summary>
    [JsonPropertyName("header")]
    public string? Header { get; set; }

    /// <summary>排序字段。</summary>
    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    /// <summary>排序方向。</summary>
    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    /// <summary>解压目标文件夹。</summary>
    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    /// <summary>创建者用户名，可为空。</summary>
    [JsonPropertyName("creator")]
    public string? CreatorName { get; set; }

    /// <summary>已访问次数。</summary>
    [JsonPropertyName("accessed")]
    public int Accessed { get; set; }
}

/// <summary>分享响应 DTO——与 Go 的 SharingResp 对应。</summary>
public sealed class ShareInfo
{
    /// <summary>分享码（用户可见 id）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>分享的文件/目录路径列表。</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>过期时间，可为空。</summary>
    [JsonPropertyName("expires")]
    public DateTimeOffset? Expires { get; set; }

    /// <summary>访问密码（是否设密），可为空。</summary>
    [JsonPropertyName("pwd")]
    public string? Pwd { get; set; }

    /// <summary>允许的最大访问次数。</summary>
    [JsonPropertyName("max_accessed")]
    public int MaxAccessed { get; set; }

    /// <summary>已访问次数。</summary>
    [JsonPropertyName("accessed")]
    public int Accessed { get; set; }

    /// <summary>是否禁用。</summary>
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    /// <summary>备注。</summary>
    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    /// <summary>分享 readme。</summary>
    [JsonPropertyName("readme")]
    public string? Readme { get; set; }

    /// <summary>分享页自定义头部。</summary>
    [JsonPropertyName("header")]
    public string? Header { get; set; }

    /// <summary>排序字段。</summary>
    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    /// <summary>排序方向。</summary>
    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    /// <summary>解压目标文件夹。</summary>
    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    /// <summary>创建者用户名。</summary>
    [JsonPropertyName("creator")]
    public string Creator { get; set; } = string.Empty;

    /// <summary>创建者角色。</summary>
    [JsonPropertyName("creator_role")]
    public int CreatorRole { get; set; }
}
