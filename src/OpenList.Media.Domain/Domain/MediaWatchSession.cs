using System.Text.Json.Serialization;

namespace OpenList.Media.Domain;

/// <summary>
/// 一次播放会话，映射数据库 x_media_watch_sessions 表。
///
/// <para>
/// 为什么不是"每次进度写回一行"：播放器每 10 秒上报一次进度，一部两小时的片子就是
/// 七百多行，其中 99% 是噪音。会话把「一次连续观看」压成一行 ——
/// **换文件 / 播完 / 关播放器**三个显式信号各结束一次会话，前端每次起播生成新的
/// <see cref="Token"/>，服务端按 (用户, token) 幂等上报，不需要额外的"开会话"往返。
/// </para>
///
/// <para>
/// 崩溃/断电这类没有结束信号的情况由**读取侧**兜底：<see cref="EndedAt"/> 为空且
/// <see cref="LastAt"/> 超过空闲阈值时，按 <see cref="LastAt"/> 视为已结束
/// （见 <c>WatchSessionService</c> 的 <c>IdleTimeout</c>）—— 不靠后台清理任务，
/// 因为一个只在读取时才有意义的字段没必要写回库。
/// </para>
/// </summary>
public sealed class MediaWatchSession
{
    /// <summary>会话主键。</summary>
    public long Id { get; set; }
    /// <summary>用户 id。</summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }
    /// <summary>媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>本次会话播放的媒体文件 id，可为空（老客户端不上报文件）。</summary>
    [JsonPropertyName("file_id")]
    public long? FileId { get; set; }
    /// <summary>客户端为这次播放生成的标识（一次播放一个），用于幂等上报。</summary>
    public string Token { get; set; } = string.Empty;
    /// <summary>会话开始时间。</summary>
    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>最后一次上报时间（心跳）。</summary>
    [JsonPropertyName("last_at")]
    public DateTimeOffset LastAt { get; set; }
    /// <summary>结束时间；为空表示"还开着"（或崩溃留下的残局，由读取侧兜底）。</summary>
    [JsonPropertyName("ended_at")]
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>起始位置（秒）。续播时不为 0。</summary>
    [JsonPropertyName("start_position_seconds")]
    public long StartPositionSeconds { get; set; }
    /// <summary>最后一次上报的位置（秒）。判断"这一小步算不算真的看了"要用它，不能用最大值。</summary>
    [JsonPropertyName("last_position_seconds")]
    public long LastPositionSeconds { get; set; }
    /// <summary>本次会话到达过的最远位置（秒）。</summary>
    [JsonPropertyName("max_position_seconds")]
    public long MaxPositionSeconds { get; set; }
    /// <summary>
    /// 累计观看秒数：只累加**向前的小步进**（拖进度条、倒退都不算），
    /// 所以它是"真的看了多久"，不是"位置到了哪"。
    /// </summary>
    [JsonPropertyName("watched_seconds")]
    public long WatchedSeconds { get; set; }
    /// <summary>是否播完（整个条目，不是单个文件）。</summary>
    public bool Completed { get; set; }

    /// <summary>
    /// 本次会话上报的片长（秒），可为空。
    ///
    /// <para>
    /// 单独存一列而不是每次去查 <see cref="MediaFile.DurationSeconds"/>：后者是扫描期探测的，
    /// 远程挂载可能探不出来（null），而客户端 `video.duration` 一定有值。比例类度量全靠它当分母，
    /// 分母缺失时宁可不算，也不能拿另一份长度凑数 —— 那会让同一部片子的比例前后不一致。
    /// </para>
    /// </summary>
    [JsonPropertyName("duration_seconds")]
    public long? DurationSeconds { get; set; }
    /// <summary>
    /// 本次会话的拖动次数（客户端 `seeking` 事件计数，按增量上报累加）。
    ///
    /// <para>
    /// 和 <see cref="WatchedSeconds"/> 合起来才说明问题：只看 3 分钟却拖了 40 次，
    /// 是"找片段"而不是"看片"。单看任一项都分不出来。
    /// </para>
    /// </summary>
    [JsonPropertyName("seek_count")]
    public int SeekCount { get; set; }
    /// <summary>
    /// 到达过的最远位置占片长的比例（0–1）。片长未知时为 0。
    ///
    /// <para>
    /// 与 <see cref="MaxPositionSeconds"/> 的区别是分母：绝对值要跟片长一起看才知道"到哪了"，
    /// 比例可以直接排序、直接和阈值比。这就是 Peach 的 `max_reached`。
    /// </para>
    /// </summary>
    [JsonPropertyName("max_reached")]
    public double MaxReached { get; set; }
    /// <summary>
    /// 结束时的位置比例（0–1）；播完记 1.0，会话还没收尾或片长未知时为空。
    ///
    /// <para>
    /// 这个字段只在收尾那一刻定稿：中途每次心跳都覆盖它的话，"离开位置"就退化成
    /// "最后一次心跳的位置"，关播放器前那几秒的拖动会被后面的心跳冲掉。
    /// 对应 Peach 的 `leave_ratio`。
    /// </para>
    /// </summary>
    [JsonPropertyName("leave_ratio")]
    public double? LeaveRatio { get; set; }
}
