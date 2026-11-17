using System.ComponentModel.DataAnnotations.Schema;
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

    /// <summary>
    /// 播放次数：每开一段新会话加一，不是每次心跳加一。
    ///
    /// <para>
    /// 口径与 Peach 的 `play_count` 一致 —— 它记的是"点了几次播放"，所以拖着玩、
    /// 反复从头看都会累加；而 <see cref="ProgressSeconds"/> 只记"最后停在哪"。
    /// </para>
    /// </summary>
    [JsonPropertyName("play_count")]
    public int PlayCount { get; set; }
    /// <summary>
    /// 累计观看秒数：跨会话累加，只含**向前的小步进**（与
    /// <see cref="MediaWatchSession.WatchedSeconds"/> 同口径）。
    ///
    /// <para>
    /// 和 <see cref="ProgressSeconds"/> 是两个量：看完一部两小时的片子再从头看十分钟，
    /// 进度是 10 分钟，观看时长是 2 小时 10 分。Peach 的 `play_seconds` 就是这个。
    /// </para>
    /// </summary>
    [JsonPropertyName("play_seconds")]
    public long PlaySeconds { get; set; }
    /// <summary>
    /// 这个用户在这部片子上到过的最远位置比例（0–1），片长未知时为 0。
    /// 与 <see cref="MediaWatchSession.MaxReached"/> 同口径，这里是跨会话取最大值。
    /// </summary>
    [JsonPropertyName("max_reached")]
    public double MaxReached { get; set; }
    /// <summary>
    /// 最近一次会话结束时的位置比例（0–1），播完为 1.0；还没有收尾的会话时为空。
    /// 对应 Peach 的 `leave_ratio`，也是「继续观看」横滑条上那条进度线的数据源。
    /// </summary>
    [JsonPropertyName("leave_ratio")]
    public double? LeaveRatio { get; set; }
    /// <summary>累计拖动次数（跨会话累加）。</summary>
    [JsonPropertyName("seek_count")]
    public int SeekCount { get; set; }
    /// <summary>
    /// 最近一次上报的片长（秒），可为空。比例类度量的分母，也是 <see cref="Skimmed"/> 的判据之一。
    /// </summary>
    [JsonPropertyName("duration_seconds")]
    public long? DurationSeconds { get; set; }

    /// <summary>"划水"阈值：最远位置超过这个比例才谈得上划水。</summary>
    public const double SkimMinReached = 0.6;
    /// <summary>"划水"容差：真实观看比例比最远位置低这么多才算划水（留出片头片尾的余量）。</summary>
    public const double SkimTolerance = 0.25;

    /// <summary>
    /// 是不是"划水"——拉到了 60% 以上，但真正看的时间比最远位置少了 25% 以上，
    /// 也就是拖过去扫了一遍而不是看了一遍。判据照搬 Peach 的 `skimmed`。
    ///
    /// <para>
    /// 没有落库：它是另外三列的函数，存下来就多一个会漂移的真相。
    /// 片长未知时一律不算划水——分母缺失时任何结论都是猜的。
    /// </para>
    /// </summary>
    [NotMapped]
    [JsonPropertyName("skimmed")]
    public bool Skimmed =>
        DurationSeconds is > 0
        && PlaySeconds > 0
        && MaxReached > SkimMinReached
        && (double)PlaySeconds / DurationSeconds.Value < MaxReached - SkimTolerance;
}
