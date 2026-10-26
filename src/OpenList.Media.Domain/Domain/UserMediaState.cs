using System.Text.Json.Serialization;

namespace OpenList.Media.Domain;

/// <summary>
/// 用户对媒体条目的个人状态，映射数据库 x_user_media_states 表。
/// 按 (UserId, ItemId) 唯一隔离——这是把单用户媒体管理（Peach 模式）
/// 适配到 OpenList.Net 多用户体系的核心表：元数据全局共享，个人状态各自独立。
/// </summary>
public sealed class UserMediaState
{
    /// <summary>状态主键。</summary>
    public long Id { get; set; }
    /// <summary>用户 id。</summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }
    /// <summary>媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>是否收藏。</summary>
    public bool Favorite { get; set; }
    /// <summary>是否已看过。</summary>
    public bool Watched { get; set; }
    /// <summary>是否加入"稍后看"队列（对应 Peach 的 watch-later）。</summary>
    [JsonPropertyName("watch_later")]
    public bool WatchLater { get; set; }
    /// <summary>是否已被用户标记（待复查/待处理，对应 Peach 的 flagged）。</summary>
    [JsonPropertyName("flagged")]
    public bool Flagged { get; set; }
    /// <summary>标记备注，可为空。</summary>
    [JsonPropertyName("flag_note")]
    public string? FlagNote { get; set; }
    /// <summary>用户评分（1-10），可为空。</summary>
    [JsonPropertyName("user_rating")]
    public int? UserRating { get; set; }
    /// <summary>播放进度（秒）。</summary>
    [JsonPropertyName("progress_seconds")]
    public long ProgressSeconds { get; set; }
    /// <summary>最近播放时间，可为空。</summary>
    [JsonPropertyName("last_played_at")]
    public DateTimeOffset? LastPlayedAt { get; set; }
    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
