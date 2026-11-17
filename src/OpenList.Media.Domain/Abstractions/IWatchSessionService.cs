using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 观看会话服务：把"看了什么、看了多久"落成可统计的事实，
/// 供观看历史与每日时长曲线使用。
///
/// <para>
/// 与 <see cref="IUserMediaStateService"/> 的分工：状态表回答「现在是什么」
/// （收藏/看过/进度），会话表回答「过去发生过什么」。两者不同源，也允许不一致 ——
/// 手动标「看过」不会凭空造出一段观看记录，反之亦然。
/// </para>
/// </summary>
public interface IWatchSessionService
{
    /// <summary>
    /// 上报一次播放心跳/边界。按 (用户, <see cref="ReportWatchReq.Token"/>) 幂等：
    /// 第一次调用建会话，后续每次更新位置与累计时长，<c>closing</c> 为真时收尾。
    /// 对已结束的会话再次上报是**静默无效**的（历史不该被事后改写）。
    ///
    /// <para>
    /// 同一次调用里把条目级聚合（<see cref="UserMediaState.PlayCount"/> 等）一并写回：
    /// 两条写入在同一个事务里落库，避免"会话记了但状态没跟上"的半截结果。
    /// 参照 Peach 的做法，一次心跳只改这几列，不触发任何缓存失效。
    /// </para>
    /// </summary>
    Task<Resp<MediaWatchSession>> ReportAsync(ReportWatchReq req, User user, CancellationToken ct = default);

    /// <summary>最近 <paramref name="days"/> 天的观看历史，按条目聚合（同一条目多次观看合并）。</summary>
    Task<Resp<List<WatchHistoryEntry>>> ListHistoryAsync(int days, int limit, User user, CancellationToken ct = default);

    /// <summary>最近 <paramref name="days"/> 天逐日的观看时长（缺数据的日子补 0，便于直接画图）。</summary>
    Task<Resp<WatchStats>> GetStatsAsync(int days, User user, CancellationToken ct = default);

    /// <summary>
    /// 删除最后活动时间早于 <paramref name="cutoff"/> 的会话，返回删除行数。
    /// 由 <c>WatchSessionRetentionHostedService</c> 按设置项 <c>media_watch_retention_days</c> 调用；
    /// 以 <c>LastAt</c>（最后心跳/结束时间）而非 <c>StartedAt</c> 判定，长会话不会在播放中被删掉。
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

/// <summary>上报一次播放心跳。</summary>
public sealed class ReportWatchReq
{
    /// <summary>客户端为本次播放生成的标识（一次播放一个，换文件就换新的）。</summary>
    public string Token { get; set; } = string.Empty;
    /// <summary>媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>当前播放的媒体文件 id，可为空。</summary>
    [JsonPropertyName("file_id")]
    public long? FileId { get; set; }
    /// <summary>当前位置（秒）。</summary>
    [JsonPropertyName("position_seconds")]
    public long PositionSeconds { get; set; }
    /// <summary>
    /// 当前片长（秒），可为空。
    ///
    /// <para>
    /// 由客户端从 <c>video.duration</c> 上报，而不是服务端去查 <c>media_files.duration_seconds</c>：
    /// 后者是扫描期探测的，远程挂载常常探不出来；而且用户可能播放的是转码流，片长以播放器为准。
    /// 缺省（null 或 ≤0）时该次上报不更新比例类度量，已有值保持不变。
    /// </para>
    /// </summary>
    [JsonPropertyName("duration_seconds")]
    public long? DurationSeconds { get; set; }
    /// <summary>
    /// 自上次上报以来新增的拖动次数（客户端累计 `seeking` 事件的增量）。
    ///
    /// <para>
    /// 传增量而不是累计值：心跳是即发即忘的，丢一发就能让累计值倒退，
    /// 而增量丢一发只是少记一次，不会把已有数据算错。
    /// </para>
    /// </summary>
    public int Seeks { get; set; }
    /// <summary>本次上报是否是一次结束（关播放器 / 换文件 / 播完）。</summary>
    public bool Closing { get; set; }
    /// <summary>是否播完（整个条目）。只在 <see cref="Closing"/> 为真时有意义。</summary>
    public bool Completed { get; set; }
}

/// <summary>
/// 观看历史里的一条（按条目聚合）。
///
/// <para>
/// 字段分两类，不要混着读：
/// </para>
/// <list type="bullet">
/// <item><b>窗口内</b>（<see cref="LastAt"/>、<see cref="WatchedSeconds"/>、<see cref="Completed"/>）
/// 只统计时间窗里的会话 —— 切到 7 天，「看了多久」就该只剩这 7 天。</item>
/// <item><b>条目级</b>（<see cref="PlayCount"/>、<see cref="MaxReached"/>、<see cref="LeaveRatio"/>、
/// <see cref="SeekCount"/>、<see cref="Skimmed"/>）取自 <see cref="UserMediaState"/>，
/// 表达的是「这部片我看成什么样了」，不随时间窗变化 —— 否则切窗口会让续播位置跟着变。</item>
/// </list>
/// </summary>
public sealed class WatchHistoryEntry
{
    /// <summary>媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>条目标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>海报路径。</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    /// <summary>发行年份。</summary>
    public int? Year { get; set; }
    /// <summary>最后一次观看时间。</summary>
    [JsonPropertyName("last_at")]
    public DateTimeOffset LastAt { get; set; }
    /// <summary>窗口内累计观看秒数。</summary>
    [JsonPropertyName("watched_seconds")]
    public long WatchedSeconds { get; set; }
    /// <summary>窗口内是否有一次看到结尾。</summary>
    public bool Completed { get; set; }
    /// <summary>播放次数（条目级）。</summary>
    [JsonPropertyName("play_count")]
    public int PlayCount { get; set; }
    /// <summary>累计观看秒数（条目级，跨会话累加，只含向前的步进）。</summary>
    [JsonPropertyName("play_seconds")]
    public long PlaySeconds { get; set; }
    /// <summary>最近一次上报的片长（秒，条目级），可为空。比例类度量的分母。</summary>
    [JsonPropertyName("duration_seconds")]
    public long? DurationSeconds { get; set; }
    /// <summary>到过的最远位置比例 0–1（条目级）。</summary>
    [JsonPropertyName("max_reached")]
    public double MaxReached { get; set; }
    /// <summary>最近一次离开位置比例 0–1（条目级），播完为 1.0。续播点用它。</summary>
    [JsonPropertyName("leave_ratio")]
    public double? LeaveRatio { get; set; }
    /// <summary>累计拖动次数（条目级）。</summary>
    [JsonPropertyName("seek_count")]
    public int SeekCount { get; set; }
    /// <summary>是否划水（条目级，判据见 <see cref="UserMediaState.Skimmed"/>）。</summary>
    public bool Skimmed { get; set; }
}

/// <summary>逐日观看时长。</summary>
public sealed class WatchDailyStat
{
    /// <summary>日期（UTC，`yyyy-MM-dd`）。</summary>
    public string Date { get; set; } = string.Empty;
    /// <summary>当天累计观看秒数。</summary>
    public long Seconds { get; set; }
}

/// <summary>观看统计。</summary>
public sealed class WatchStats
{
    /// <summary>窗口天数（归一后的值，前端据此标注口径）。</summary>
    [JsonPropertyName("window_days")]
    public int WindowDays { get; set; }
    /// <summary>窗口内总观看秒数。</summary>
    [JsonPropertyName("total_seconds")]
    public long TotalSeconds { get; set; }
    /// <summary>逐日明细（从窗口起点到今天，缺的补 0）。</summary>
    public List<WatchDailyStat> Days { get; set; } = [];
}
