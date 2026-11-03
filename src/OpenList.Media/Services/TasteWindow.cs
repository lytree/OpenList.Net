using OpenList.Media.Domain;

namespace OpenList.Media.Services;

/// <summary>
/// 口味画像的时间窗口口径（`GET /media/taste?days=`）。
///
/// <para>
/// 有观看会话的条目以 <c>x_media_watch_sessions</c> 为准：只要窗口内有过一次会话，
/// 就算"最近 N 天内看过"；窗口外只有旧会话的条目不再计入 —— 哪怕它的状态行
/// （收藏/评分）刚被改过。会话是真实播放事件，状态行只是"现在是什么"，二者不同源。
/// </para>
///
/// <para>
/// 完全没有会话的条目（接通会话表之前的历史数据、或客户端未上报心跳）回退到旧口径
/// <see cref="InWindow(DateTimeOffset?, DateTimeOffset, DateTimeOffset?)"/>：
/// 取 <c>LastPlayedAt</c> / <c>UpdatedAt</c> 中<b>较晚</b>的时间。这样旧库不会整页变空，
/// 同时又有会话的新数据不再被"随便改一下收藏"拉进/踢出窗口。
/// </para>
/// </summary>
public static class TasteWindow
{
    /// <summary>窗口天数上限（10 年）。再大的值等价于不限，但仍回显具体天数。</summary>
    public const int MaxDays = 3650;

    /// <summary>
    /// 把查询参数 <c>days</c> 归一成窗口天数：缺省 / 非正数 / 非法值一律视为 <b>0 = 不限</b>，
    /// 超过 <see cref="MaxDays"/> 的按上限裁剪。归一后的值会原样回显到响应的 <c>windowDays</c>。
    /// </summary>
    public static int Normalize(int? days)
        => days is null or <= 0 ? 0 : Math.Min(days.Value, MaxDays);

    /// <summary>
    /// 窗口起点（含）。不限时返回 <c>null</c>，调用方据此跳过过滤而不是拿一个极早的时间去比较
    /// ——那样会把 <c>UpdatedAt</c> 从未赋值的旧行（默认值）也静默排除掉。
    /// </summary>
    public static DateTimeOffset? Cutoff(int? days, DateTimeOffset now)
    {
        var d = Normalize(days);
        return d == 0 ? null : now.AddDays(-d);
    }

    /// <summary>一行状态最近一次被"动过"的时间：<c>LastPlayedAt</c> 与 <c>UpdatedAt</c> 取较晚者。</summary>
    public static DateTimeOffset Latest(DateTimeOffset? lastPlayedAt, DateTimeOffset updatedAt)
        => lastPlayedAt is { } played && played > updatedAt ? played : updatedAt;

    /// <summary>这一行是否落在窗口内。<paramref name="cutoff"/> 为 null 表示不限，恒为 true。</summary>
    public static bool InWindow(DateTimeOffset? lastPlayedAt, DateTimeOffset updatedAt, DateTimeOffset? cutoff)
        => cutoff is null || Latest(lastPlayedAt, updatedAt) >= cutoff.Value;

    /// <summary>
    /// 会话口径的窗口判定：有会话的条目完全按会话算（窗口内有无会话），
    /// 没有会话的旧条目才回退到状态行时间。不限时恒为 true。
    /// </summary>
    public static bool InWindow(
        bool hasAnySession, bool hasSessionInWindow,
        DateTimeOffset? lastPlayedAt, DateTimeOffset updatedAt, DateTimeOffset? cutoff)
        => cutoff is null
            || (hasAnySession ? hasSessionInWindow : InWindow(lastPlayedAt, updatedAt, cutoff));

    /// <summary>
    /// 同一 (用户, 条目) 的多次观看合并成窗口口径：窗口内会话的观看时长<b>累加</b>；
    /// "最近观看时间"取窗口内会话的 <c>EndedAt ?? LastAt</c> 最大值
    /// （会话还开着时以最后心跳为准，崩溃留下的残局不会显示成"刚刚看过"）。
    /// 窗口按会话的 <c>StartedAt</c> 归属，与观看历史 / 逐日统计保持同一口径。
    /// </summary>
    public static SessionWindow MergeSessions(IEnumerable<MediaWatchSession> sessions, DateTimeOffset? cutoff)
    {
        var all = sessions.ToList();
        var inWindow = cutoff is null
            ? all
            : all.Where(s => s.StartedAt >= cutoff.Value).ToList();
        return new SessionWindow(
            HasAny: all.Count > 0,
            HasInWindow: inWindow.Count > 0,
            WatchedSeconds: inWindow.Sum(s => s.WatchedSeconds),
            LastAt: inWindow.Count == 0 ? null : inWindow.Max(s => s.EndedAt ?? s.LastAt));
    }
}

/// <summary>一个条目的会话在窗口口径下的合并结果。</summary>
public readonly record struct SessionWindow(
    bool HasAny, bool HasInWindow, long WatchedSeconds, DateTimeOffset? LastAt);
