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
    /// </summary>
    Task<Resp<MediaWatchSession>> ReportAsync(ReportWatchReq req, User user, CancellationToken ct = default);

    /// <summary>最近 <paramref name="days"/> 天的观看历史，按条目聚合（同一条目多次观看合并）。</summary>
    Task<Resp<List<WatchHistoryEntry>>> ListHistoryAsync(int days, int limit, User user, CancellationToken ct = default);

    /// <summary>最近 <paramref name="days"/> 天逐日的观看时长（缺数据的日子补 0，便于直接画图）。</summary>
    Task<Resp<WatchStats>> GetStatsAsync(int days, User user, CancellationToken ct = default);
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
    /// <summary>本次上报是否是一次结束（关播放器 / 换文件 / 播完）。</summary>
    public bool Closing { get; set; }
    /// <summary>是否播完（整个条目）。只在 <see cref="Closing"/> 为真时有意义。</summary>
    public bool Completed { get; set; }
}

/// <summary>观看历史里的一条（按条目聚合）。</summary>
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
