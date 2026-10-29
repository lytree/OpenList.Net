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
}
