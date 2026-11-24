using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 关注创作者 → 新作品更新流。
/// <para>
/// 单用户产品：没有 UserId，「关注了谁」是全站事实。写操作（关注 / 取关 / 检查更新）
/// 要求已登录；读接口（关注列表 / 更新流 / 关注状态）对游客开放，但私有库里的人物
/// 与条目照旧不可见（人物按「不存在」处理，条目不回填 <c>ItemId</c>）。
/// </para>
/// <para>
/// 「新作品」的口径：每次检查把数据源里**首次见到**的作品写成一行
/// <see cref="MediaFollowEntry"/>（(人物, 外部作品 id) 唯一），更新流就是这些行按
/// 首次见到时间倒序。首次关注时的检查会把该人物的全部作品落成条目 ——
/// 它们对这位用户来说确实是「首次见到」。
/// </para>
/// </summary>
public interface IMediaFollowService
{
    /// <summary>
    /// 批量取关注状态。对当前用户不可见的人物不出现在结果里
    /// （私有库人物对游客按「不存在」处理，不能借这个接口确认其存在）。
    /// </summary>
    Task<Resp<Dictionary<long, bool>>> GetStateAsync(IReadOnlyCollection<long> personIds, User user, CancellationToken ct = default);

    /// <summary>
    /// 关注一位创作者（幂等：已关注时直接返回已有行）。人物不存在 404；没有 TMDB id 400。
    /// 首次关注成功后立刻检查一次，把该人物现有的全部作品落成更新流条目。
    /// </summary>
    Task<Resp<MediaFollow>> FollowAsync(long personId, User user, CancellationToken ct = default);

    /// <summary>取消关注：删关注行及其全部更新流条目（幂等，未关注也返回成功）。</summary>
    Task<Resp> UnfollowAsync(long personId, User user, CancellationToken ct = default);

    /// <summary>已关注的创作者列表（含最近检查时间、失败原因与「还没入库」的新作品数）。</summary>
    Task<Resp<List<FollowedPerson>>> ListFollowedAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// 更新流：按 <see cref="MediaFollowEntry.FirstSeenAt"/> 倒序分页。
    /// <paramref name="personId"/> 可限定只看某一位；该人物对当前用户不可见时 404。
    /// 已入库的条目带 <c>ItemId</c>，已加入想看的带 <c>WantId</c>。
    /// </summary>
    Task<Resp<PageResp<FollowFeedEntry>>> GetFeedAsync(int page, int perPage, long? personId, User user, CancellationToken ct = default);

    /// <summary>
    /// 手动检查更新。给 <paramref name="personId"/> 就同步检查这一位并返回实际结果；
    /// 不给就对全部关注在后台跑，立即返回 <see cref="FollowCheckResult.Started"/> = true。
    /// </summary>
    Task<Resp<FollowCheckResult>> CheckAsync(long? personId, User user, CancellationToken ct = default);

    /// <summary>
    /// 对全部关注逐个检查（宿主任务与手动后台检查共用）。每个人物之间留出间隔，
    /// 单个人物失败只记 <see cref="MediaFollow.LastError"/>，不中断整轮。
    /// </summary>
    /// <returns>本轮新增的更新流条目数。</returns>
    Task<int> CheckAllAsync(CancellationToken ct = default);
}

/// <summary>关注列表项：创作者 + 关注状态。</summary>
public sealed class FollowedPerson
{
    /// <summary>创作者 id。</summary>
    [JsonPropertyName("person_id")]
    public long PersonId { get; set; }
    /// <summary>姓名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>头像路径，可为空。</summary>
    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
    /// <summary>TMDB person id，可为空（为空的人物无法检查更新）。</summary>
    [JsonPropertyName("tmdb_id")]
    public string? TmdbId { get; set; }
    /// <summary>关注时间。</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>最近一次**成功**检查的时间；为空表示还没成功检查过。</summary>
    [JsonPropertyName("last_checked_at")]
    public DateTimeOffset? LastCheckedAt { get; set; }
    /// <summary>最近一次检查的失败原因（成功时为空）。</summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }
    /// <summary>库里还没有的作品数（只算当前用户可见的库；游客看不到的私有库条目按「未入库」计）。</summary>
    [JsonPropertyName("new_count")]
    public int NewCount { get; set; }
}

/// <summary>更新流里的一条作品。</summary>
public sealed class FollowFeedEntry
{
    /// <summary>更新流条目 id。</summary>
    public long Id { get; set; }
    /// <summary>创作者 id。</summary>
    [JsonPropertyName("person_id")]
    public long PersonId { get; set; }
    /// <summary>创作者姓名。</summary>
    [JsonPropertyName("person_name")]
    public string PersonName { get; set; } = string.Empty;
    /// <summary>创作者头像路径，可为空。</summary>
    [JsonPropertyName("person_profile_path")]
    public string? PersonProfilePath { get; set; }
    /// <summary>作品类型：<c>movie</c> / <c>tv</c>。</summary>
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;
    /// <summary>作品外部 id（TMDB id 字符串）。</summary>
    [JsonPropertyName("external_id")]
    public string ExternalId { get; set; } = string.Empty;
    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>首次上映/播出日期 <c>YYYY-MM-DD</c>，可为空。</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
    /// <summary>海报绝对 URL，可为空。</summary>
    [JsonPropertyName("poster_url")]
    public string? PosterUrl { get; set; }
    /// <summary>该人物在这部作品里的角色/职位，可为空。</summary>
    public string? Role { get; set; }
    /// <summary>首次见到（进入更新流）的时间。</summary>
    [JsonPropertyName("first_seen_at")]
    public DateTimeOffset FirstSeenAt { get; set; }
    /// <summary>已入库且对当前用户可见的条目 id；为空 = 还没入库。</summary>
    [JsonPropertyName("item_id")]
    public long? ItemId { get; set; }
    /// <summary>已加入想看（且尚未入库）的想看记录 id；为空 = 没加过想看。</summary>
    [JsonPropertyName("want_id")]
    public long? WantId { get; set; }
}

/// <summary>一次检查更新的结果。</summary>
public sealed class FollowCheckResult
{
    /// <summary>本次实际检查的人物数（后台启动时为 0）。</summary>
    [JsonPropertyName("checked_persons")]
    public int CheckedPersons { get; set; }
    /// <summary>本次新增的更新流条目数（后台启动时为 0）。</summary>
    [JsonPropertyName("new_entries")]
    public int NewEntries { get; set; }
    /// <summary>是否已转入后台执行（此时上面两个计数无意义，稍后刷新关注列表即可）。</summary>
    public bool Started { get; set; }
}
