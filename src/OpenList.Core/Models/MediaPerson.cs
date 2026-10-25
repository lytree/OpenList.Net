using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 创作者（导演/演员/作者等），映射数据库 x_media_persons 表。
/// 全局共享，按名称 + TMDB id 去重，供创作者聚合视图使用。
/// </summary>
public sealed class MediaPerson
{
    /// <summary>创作者主键。</summary>
    public long Id { get; set; }
    /// <summary>姓名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>TMDB person id，可为空。</summary>
    [JsonPropertyName("tmdb_id")]
    public string? TmdbId { get; set; }
    /// <summary>头像路径（VFS 或外部 URL），可为空。</summary>
    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 媒体条目与创作者的关联，映射数据库 x_media_item_persons 表。
/// </summary>
public sealed class MediaItemPerson
{
    /// <summary>关联主键。</summary>
    public long Id { get; set; }
    /// <summary>媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>创作者 id。</summary>
    [JsonPropertyName("person_id")]
    public long PersonId { get; set; }
    /// <summary>角色类型（director / actor / writer / creator 等）。</summary>
    public string Role { get; set; } = string.Empty;
    /// <summary>饰演角色名（演员场景），可为空。</summary>
    public string? Character { get; set; }
    /// <summary>排序（演员表顺序）。</summary>
    public int Order { get; set; }
}
