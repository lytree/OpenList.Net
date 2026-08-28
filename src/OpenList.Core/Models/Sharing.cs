using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 分享实体。与 Go model.Sharing 对应。使用 long 类型的自增主键 <see cref="Id"/>，
/// 而前端使用的 string 类型 <c>id</c> 字段则是用户可见的分享码，存于 <see cref="ShareCode"/>。
/// </summary>
public sealed class Sharing
{
    // 内部数据库主键。用户可见的分享 id（Go: string id）为 ShareCode，
    // 它通过 JsonPropertyName 序列化为 "id"——因此这个内部 Id 不能被序列化为 JSON，
    // 否则会与 ShareCode 的 "id" 冲突并破坏序列化。
    [JsonIgnore]
    public long Id { get; set; }

    /// <summary>用户可见的分享 id（Go: string id）。为空时由服务器生成。</summary>
    [JsonPropertyName("id")]
    public string ShareCode { get; set; } = string.Empty;

    /// <summary>分享的源路径（单文件/单目录分享）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>多路径分享时拼接后的存储（Go: Files []string），以 JSON 形式存储。</summary>
    public string FilesJson { get; set; } = "[]";

    /// <summary>分享访问密码，可为空。</summary>
    [JsonPropertyName("pwd")]
    public string? Password { get; set; }

    /// <summary>分享过期时间，可为空（表示永不过期）。</summary>
    [JsonPropertyName("expires")]
    public DateTimeOffset? Expiration { get; set; }

    /// <summary>允许的最大访问次数（0 表示不限）。</summary>
    [JsonPropertyName("max_accessed")]
    public int MaxAccessed { get; set; }

    /// <summary>已访问次数。</summary>
    [JsonPropertyName("accessed")]
    public int Accessed { get; set; }

    /// <summary>是否被禁用。</summary>
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    /// <summary>分享备注。</summary>
    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    /// <summary>分享首页的 readme 说明文字。</summary>
    [JsonPropertyName("readme")]
    public string? Readme { get; set; }

    /// <summary>分享页自定义头部内容。</summary>
    [JsonPropertyName("header")]
    public string? Header { get; set; }

    /// <summary>分享列表排序字段。</summary>
    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    /// <summary>分享列表排序方向。</summary>
    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    /// <summary>分享解压目标文件夹。</summary>
    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    /// <summary>创建该分享的所有者用户主键。</summary>
    public long OwnerId { get; set; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>分享是否启用（<see cref="Disabled"/> 的反义，便于调用方阅读）。</summary>
    [JsonIgnore]
    public bool Enabled { get => !Disabled; set => Disabled = !value; }
}
