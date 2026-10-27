using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 媒体目录聚合查询服务：提供筛选条、人物页、统计页与首页所需的只读聚合数据。
/// 与 <see cref="IMediaLibraryService"/> 的单条目/单页查询分开——这些端点一次要扫全库做分组，
/// 放在列表服务里会让每次翻页都背上全表聚合的开销。
/// 所有结果都按 <see cref="User.BasePath"/> 过滤，与条目列表口径一致。
/// </summary>
public interface IMediaCatalogService
{
    /// <summary>题材与年份的聚合计数（筛选条数据源）。<paramref name="libraryId"/> 为空表示全部可见库。</summary>
    Task<Resp<MediaFacets>> GetFacetsAsync(long? libraryId, User user, CancellationToken ct = default);

    /// <summary>创作者列表（按关联条目数降序）。<paramref name="role"/> 可限定 director / actor 等。</summary>
    Task<Resp<PageResp<MediaPersonSummary>>> ListPersonsAsync(string? keyword, string? role, int page, int perPage, User user, CancellationToken ct = default);

    /// <summary>某创作者关联的条目（分页）。</summary>
    Task<Resp<PageResp<MediaItem>>> ListPersonItemsAsync(long personId, int page, int perPage, User user, CancellationToken ct = default);

    /// <summary>片商列表（按关联条目数降序，番号域聚合维度）。</summary>
    Task<Resp<PageResp<MediaTaxonomySummary>>> ListStudiosAsync(string? keyword, int page, int perPage, User user, CancellationToken ct = default);

    /// <summary>单个片商（详情页标题/条目数；不存在时 <c>404</c>）。</summary>
    Task<Resp<MediaTaxonomySummary>> GetStudioAsync(long studioId, User user, CancellationToken ct = default);

    /// <summary>某片商名下的条目（分页）。</summary>
    Task<Resp<PageResp<MediaItem>>> ListStudioItemsAsync(long studioId, int page, int perPage, User user, CancellationToken ct = default);

    /// <summary>系列列表（按关联条目数降序，番号域聚合维度）。</summary>
    Task<Resp<PageResp<MediaTaxonomySummary>>> ListSeriesAsync(string? keyword, int page, int perPage, User user, CancellationToken ct = default);

    /// <summary>单个系列（详情页标题/条目数；不存在时 <c>404</c>）。</summary>
    Task<Resp<MediaTaxonomySummary>> GetSeriesAsync(long seriesId, User user, CancellationToken ct = default);

    /// <summary>某系列名下的条目（分页）。</summary>
    Task<Resp<PageResp<MediaItem>>> ListSeriesItemsAsync(long seriesId, int page, int perPage, User user, CancellationToken ct = default);

    /// <summary>统计概览：数量、体积、时长、年度/题材/评分分布。<paramref name="libraryId"/> 为空表示全部可见库。</summary>
    Task<Resp<MediaStats>> GetStatsAsync(long? libraryId, User user, CancellationToken ct = default);

    /// <summary>首页聚合：最近入库、继续观看、最近收藏。</summary>
    Task<Resp<MediaHome>> GetHomeAsync(int limit, User user, CancellationToken ct = default);
}

/// <summary>题材/年份聚合结果。</summary>
public sealed class MediaFacets
{
    /// <summary>参与聚合的条目总数。</summary>
    public int Total { get; set; }
    /// <summary>题材及各自的条目数（按条目数降序）。</summary>
    public List<MediaFacetCount> Genres { get; set; } = [];
    /// <summary>发行年份及各自的条目数（按年份降序）。</summary>
    public List<MediaFacetCount> Years { get; set; } = [];
}

/// <summary>单个聚合桶。</summary>
public sealed class MediaFacetCount
{
    /// <summary>桶名（题材名或年份的字符串形态）。</summary>
    public string Value { get; set; } = string.Empty;
    /// <summary>条目数。</summary>
    public int Count { get; set; }
}

/// <summary>创作者列表项：创作者本身 + 在当前用户可见范围内的关联条目数。</summary>
public sealed class MediaPersonSummary
{
    /// <summary>创作者 id。</summary>
    public long Id { get; set; }
    /// <summary>姓名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>TMDB person id，可为空。</summary>
    [JsonPropertyName("tmdb_id")]
    public string? TmdbId { get; set; }
    /// <summary>头像路径，可为空。</summary>
    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
    /// <summary>关联条目数（仅统计当前用户可见的条目）。</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }
    /// <summary>出现过的角色类型（director / actor / writer 等）。</summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>片商/系列列表项：维度本身 + 在当前用户可见范围内的关联条目数。</summary>
public sealed class MediaTaxonomySummary
{
    /// <summary>片商/系列 id。</summary>
    public long Id { get; set; }
    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>关联条目数（仅统计当前用户可见的条目）。</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }
}

/// <summary>媒体统计概览。</summary>
public sealed class MediaStats
{
    /// <summary>可见媒体库数量。</summary>
    [JsonPropertyName("library_count")]
    public int LibraryCount { get; set; }
    /// <summary>可见条目数量。</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }
    /// <summary>可见媒体文件数量。</summary>
    [JsonPropertyName("file_count")]
    public int FileCount { get; set; }
    /// <summary>媒体文件总体积（字节）。</summary>
    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }
    /// <summary>已知时长合计（秒）。未探测时长的文件不计入。</summary>
    [JsonPropertyName("total_duration_seconds")]
    public long TotalDurationSeconds { get; set; }
    /// <summary>当前用户已看条目数。</summary>
    [JsonPropertyName("watched_count")]
    public int WatchedCount { get; set; }
    /// <summary>当前用户收藏条目数。</summary>
    [JsonPropertyName("favorite_count")]
    public int FavoriteCount { get; set; }
    /// <summary>按年份的条目数（降序）。</summary>
    [JsonPropertyName("by_year")]
    public List<MediaFacetCount> ByYear { get; set; } = [];
    /// <summary>按题材的条目数（降序）。</summary>
    [JsonPropertyName("by_genre")]
    public List<MediaFacetCount> ByGenre { get; set; } = [];
    /// <summary>源站评分分布。</summary>
    public List<MediaRatingBucket> Ratings { get; set; } = [];
}

/// <summary>评分分布桶。</summary>
public sealed class MediaRatingBucket
{
    /// <summary>区间标签，如 <c>8.0-8.9</c>。</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>落在该区间的条目数。</summary>
    public int Count { get; set; }
}

/// <summary>首页聚合结果。三个货架各自独立取数，任一侧为空都不影响其它两个。</summary>
public sealed class MediaHome
{
    /// <summary>最近入库（按创建时间倒序）。</summary>
    [JsonPropertyName("recently_added")]
    public List<MediaItem> RecentlyAdded { get; set; } = [];
    /// <summary>继续观看（有进度且未看完，按最近播放时间倒序）。</summary>
    [JsonPropertyName("continue_watching")]
    public List<MediaItem> ContinueWatching { get; set; } = [];
    /// <summary>最近收藏（按状态更新时间倒序）。</summary>
    [JsonPropertyName("recently_favorited")]
    public List<MediaItem> RecentlyFavorited { get; set; } = [];
}
