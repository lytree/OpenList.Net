namespace OpenList.Media.Services;

/// <summary>
/// 口味画像的时间窗口口径（`GET /media/taste?days=`）。
///
/// <para>
/// 状态表是 <em>每 (用户, 条目) 一行</em>，只存"当前是什么"，不存"什么时候变成这样"：
/// 没有 <c>watched_at</c> / <c>favorited_at</c>，只有整行的 <c>UpdatedAt</c>
/// 与播放时刷新的 <c>LastPlayedAt</c>。所以窗口只能是
/// <b>「最近 N 天内动过这一行」</b>，而不是"最近 N 天内被标为看过"——这条差异写进
/// 接口文档与前端文案，别让用户以为它是精确的事件流统计。
/// </para>
///
/// <para>
/// 判定用两者中<b>较晚</b>的时间：收藏/评分只刷新 <c>UpdatedAt</c>，
/// 播放进度只在这一行既有状态上叠加，两个字段谁新都可能，取 max 才不会漏。
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
}
