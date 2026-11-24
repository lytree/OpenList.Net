using System.Text.Json.Serialization;

namespace OpenList.Media.Domain;

/// <summary>
/// 关注一位创作者（<see cref="MediaPerson"/>），映射数据库 x_media_follows 表。
/// <para>
/// 单用户产品：没有 UserId 列，「关注了谁」就是全站事实。一个人物最多一行
/// （<c>PersonId</c> 唯一索引）。取关 = 删行，连同 <see cref="MediaFollowEntry"/> 一起清掉。
/// </para>
/// <para>
/// 「有没有新作品」不在关注行上累加，而是落在 <see cref="MediaFollowEntry"/>：
/// 每次检查把数据源（TMDB combined_credits）里**首次见到**的作品写一行，
/// 更新流就是「这些行按首次见到时间倒序」。
/// </para>
/// </summary>
public sealed class MediaFollow
{
    /// <summary>关注主键。</summary>
    public long Id { get; set; }
    /// <summary>被关注的创作者 id。</summary>
    [JsonPropertyName("person_id")]
    public long PersonId { get; set; }
    /// <summary>关注时间。</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>最近一次检查更新的时间；为空表示还没检查过（页面可提示「尚未检查」）。</summary>
    [JsonPropertyName("last_checked_at")]
    public DateTimeOffset? LastCheckedAt { get; set; }
    /// <summary>最近一次检查的失败原因（成功时清空）。</summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }
}

/// <summary>
/// 关注更新流里的一条「作品」，映射数据库 x_media_follow_entries 表。
/// <para>
/// 一个 (人物, 外部作品 id) 只有一行 —— 这是检查任务做差集的依据：
/// 数据源返回的作品里，库里没有的才插入；因此「首次见到时间」就是它进入更新流的时刻，
/// 而不是作品自身的发行日期（发行日期可能被 retro-fill 或回填）。
/// </para>
/// <para>
/// <see cref="ItemId"/> 在**读取时**按 <c>TmdbId</c> 与 <see cref="MediaItem"/> 对表得出，不落库：
/// 扫描/刮削随时可能把这部作品入库，落库就得再维护一条同步链。
/// </para>
/// </summary>
public sealed class MediaFollowEntry
{
    /// <summary>主键。</summary>
    public long Id { get; set; }
    /// <summary>所属关注（= 被关注的创作者）id。</summary>
    [JsonPropertyName("follow_id")]
    public long FollowId { get; set; }
    /// <summary>创作者 id（冗余自 <see cref="MediaFollow.PersonId"/>，便于按人物聚合查询）。</summary>
    [JsonPropertyName("person_id")]
    public long PersonId { get; set; }
    /// <summary>数据源里的作品类型：<c>movie</c> / <c>tv</c>。</summary>
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;
    /// <summary>数据源里的作品 id（TMDB id 字符串，与 <see cref="MediaItem.TmdbId"/> 同形）。</summary>
    [JsonPropertyName("external_id")]
    public string ExternalId { get; set; } = string.Empty;
    /// <summary>作品标题（检查当时的快照）。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>首次上映/播出日期（数据源给的原样字符串 <c>YYYY-MM-DD</c>，可为空）。</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
    /// <summary>海报路径（TMDB 外链或空；前端经 <c>/api/media/image?src=</c> 代理，不直连外网）。</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    /// <summary>该人物在这部作品里的角色/职位（actor 的 character、director 的 "Director"）。</summary>
    public string? Role { get; set; }
    /// <summary>首次见到（进入更新流）的时间。</summary>
    [JsonPropertyName("first_seen_at")]
    public DateTimeOffset FirstSeenAt { get; set; }
}
