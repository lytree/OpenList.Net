using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 媒体元数据服务：调度刮削（搜索→确认→写回），把 provider 结果落到
/// x_media_items / x_media_persons，并生成 Kodi 兼容 NFO 与海报文件写回 VFS
/// （与 Jellyfin 等媒体中心直接互通）。
/// </summary>
public interface IMediaMetadataService
{
    /// <summary>
    /// 刮削单个条目（自动搜索最佳匹配并写回）。
    /// <paramref name="providerName"/> 显式指定 provider（如 <c>tmdb</c> / <c>code</c>）；
    /// 省略时自动选择：条目已有番号且 code provider 已配置则走 code，否则走默认（TMDB）。
    /// </summary>
    Task<Resp<MediaItem>> ScrapeItemAsync(long itemId, User user, string? providerName = null, CancellationToken ct = default);
    /// <summary>用人工指定的 provider 条目 id 修正匹配并写回。</summary>
    Task<Resp<MediaItem>> MatchItemAsync(long itemId, string providerId, User user, CancellationToken ct = default);
    /// <summary>
    /// 批量刮削库内所有待处理条目（后台任务化，立即返回）。
    /// 同一库同时只允许一轮：已在跑时返回 409，没有待刮削条目时返回 400。
    /// </summary>
    Task<Resp> ScrapeLibraryAsync(long libraryId, User user, CancellationToken ct = default);
    /// <summary>
    /// 各库的刮削状态（管理台「整库刮削」按钮的数据源）。
    /// 一次返回全部可见库，而不是按库逐个查 —— 管理台 2.5 秒轮询一次，
    /// 逐库请求会有 N 倍开销。
    /// </summary>
    Task<Resp<List<MediaScrapeStatus>>> ListScrapeTasksAsync(User user, CancellationToken ct = default);
    /// <summary>列出条目的创作者（聚合视图数据源）。</summary>
    Task<Resp<List<MediaItemPersonCredit>>> ListItemCreditsAsync(long itemId, User user, CancellationToken ct = default);
    /// <summary>创作者聚合：按创作者列出其名下条目（当前用户可见范围内）。</summary>
    Task<Resp<PersonAggregate>> GetPersonAsync(long personId, User user, CancellationToken ct = default);
}

/// <summary>条目的创作者条目（含创作者信息）。</summary>
public sealed class MediaItemPersonCredit
{
    /// <summary>创作者 id。</summary>
    public long PersonId { get; set; }
    /// <summary>姓名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>头像路径。</summary>
    public string? ProfilePath { get; set; }
    /// <summary>角色类型。</summary>
    public string Role { get; set; } = string.Empty;
    /// <summary>饰演角色名。</summary>
    public string? Character { get; set; }
    /// <summary>演员表排序。</summary>
    public int Order { get; set; }
}

/// <summary>创作者聚合视图。</summary>
public sealed class PersonAggregate
{
    /// <summary>创作者信息。</summary>
    public MediaPerson Person { get; set; } = new();
    /// <summary>名下条目（含参与角色）。</summary>
    public List<PersonAggregateItem> Items { get; set; } = [];
}

/// <summary>创作者名下的一条作品。</summary>
public sealed class PersonAggregateItem
{
    /// <summary>媒体条目。</summary>
    public MediaItem Item { get; set; } = new();
    /// <summary>参与角色类型。</summary>
    public string Role { get; set; } = string.Empty;
    /// <summary>饰演角色名。</summary>
    public string? Character { get; set; }
}

/// <summary>
/// 单个媒体库的刮削状态。
///
/// 三个计数不是同一件事，管理台必须分开看：
/// <see cref="Pending"/> 会被批量任务消费；<see cref="Unmatched"/> 表示自动匹配失败过，
/// 批量任务**不会**再碰它们（重试只能逐条走详情页）；<see cref="Matched"/> 才是真正完成的。
/// 只报「总数」的话，「点完刮削没反应」和「都已经刮完了」在界面上长得一模一样。
/// </summary>
public sealed class MediaScrapeStatus
{
    /// <summary>媒体库 id。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
    /// <summary>媒体库名称。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("library_name")]
    public string LibraryName { get; set; } = string.Empty;
    /// <summary>等待刮削的条目数（批量任务的取数范围）。</summary>
    public int Pending { get; set; }
    /// <summary>自动匹配失败、批量任务不会再处理的条目数。</summary>
    public int Unmatched { get; set; }
    /// <summary>已写入元数据的条目数。</summary>
    public int Matched { get; set; }
    /// <summary>是否有批量刮削正在运行。</summary>
    public bool Running { get; set; }
    /// <summary>本轮计划处理的条目数；未运行时为 0。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("batch_size")]
    public int BatchSize { get; set; }
    /// <summary>本轮已处理条目数；未运行时为 0。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("batch_done")]
    public int BatchDone { get; set; }
    /// <summary>本轮累计失败条目数；未运行时为 0。</summary>
    public int Failed { get; set; }
    /// <summary>本轮最近一次失败的错误信息，可为空。</summary>
    public string? Error { get; set; }
}
