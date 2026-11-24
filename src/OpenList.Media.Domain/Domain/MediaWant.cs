using System.Text.Json.Serialization;

namespace OpenList.Media.Domain;

/// <summary>
/// 「库里还没有、但我想看」的外部作品，映射数据库 x_media_wants 表。
/// <para>
/// 与 <see cref="UserMediaState.WatchLater"/> 是两件事：后者是**已入库条目**上的「稍后再看」；
/// 本表是**库外作品**（来自关注更新流 / TMDB 发现），还没有文件，走「找资源 → 下载 → 扫描入库」。
/// </para>
/// <para>
/// 入库后 <see cref="ItemId"/> 回填为真实条目 id，想看页据此隐藏它（它已经是库里的一员了），
/// 但行不删：下载记录、关注流都要靠它把「当初想看的那部」与「入库的那条」对上。
/// </para>
/// </summary>
public sealed class MediaWant
{
    /// <summary>主键。</summary>
    public long Id { get; set; }
    /// <summary>数据源作品 id（TMDB id 字符串；入库后与 <see cref="MediaItem.TmdbId"/> 对表）。</summary>
    [JsonPropertyName("external_id")]
    public string ExternalId { get; set; } = string.Empty;
    /// <summary><c>movie</c> / <c>tv</c>。</summary>
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "movie";
    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>上映/首播年份，可为空。</summary>
    public int? Year { get; set; }
    /// <summary>海报路径（TMDB 相对路径或外链）。</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    /// <summary>一句简介。</summary>
    public string? Overview { get; set; }
    /// <summary>上映/首播日期 <c>YYYY-MM-DD</c>，可为空。</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
    /// <summary>加入想看的时间。</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>入库后的条目 id；为空 = 还没入库。</summary>
    [JsonPropertyName("item_id")]
    public long? ItemId { get; set; }
}
