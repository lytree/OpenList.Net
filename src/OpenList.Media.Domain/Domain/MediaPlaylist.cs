using System.Text.Json.Serialization;

namespace OpenList.Media.Domain;

/// <summary>
/// 播放列表（合集），映射数据库 x_media_playlists 表。
/// 与媒体库不同，播放列表是<em>私有</em>的：归属于创建它的用户，不参与全局共享，
/// 也不能跨用户读写——这是把 Peach 的单用户"播放列表"落到多用户体系的方式。
/// 条目本身仍走可见性校验，用户只能把自己看得到的条目放进自己的列表。
/// </summary>
public sealed class MediaPlaylist
{
    /// <summary>播放列表主键。</summary>
    public long Id { get; set; }
    /// <summary>所有者用户 id。</summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }
    /// <summary>列表名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>描述，可为空。</summary>
    public string? Description { get; set; }
    /// <summary>创建时间。</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间（改名、增删片、重排都会刷新）。</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 播放列表成员，映射数据库 x_media_playlist_items 表。
/// 用自增 <see cref="Position"/> 维护顺序：重排时先整体挪到临时高位区间再回填，
/// 避免 UNIQUE(PlaylistId, Position) 在原位交换时撞位。
/// </summary>
public sealed class MediaPlaylistItem
{
    /// <summary>成员主键。</summary>
    public long Id { get; set; }
    /// <summary>所属播放列表 id。</summary>
    [JsonPropertyName("playlist_id")]
    public long PlaylistId { get; set; }
    /// <summary>媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>在列表中的位置（从 0 开始，越小越靠前）。</summary>
    public int Position { get; set; }
    /// <summary>加入时间。</summary>
    [JsonPropertyName("added_at")]
    public DateTimeOffset AddedAt { get; set; }
}
