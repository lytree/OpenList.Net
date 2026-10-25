using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 媒体元数据服务：调度刮削（搜索→确认→写回），把 provider 结果落到
/// x_media_items / x_media_persons，并生成 Kodi 兼容 NFO 与海报文件写回 VFS
/// （与 Jellyfin 等媒体中心直接互通）。
/// </summary>
public interface IMediaMetadataService
{
    /// <summary>刮削单个条目（自动搜索最佳匹配并写回）。</summary>
    Task<Resp<MediaItem>> ScrapeItemAsync(long itemId, User user, CancellationToken ct = default);
    /// <summary>用人工指定的 provider 条目 id 修正匹配并写回。</summary>
    Task<Resp<MediaItem>> MatchItemAsync(long itemId, string providerId, User user, CancellationToken ct = default);
    /// <summary>批量刮削库内所有待处理条目（后台任务化，立即返回）。</summary>
    Task<Resp> ScrapeLibraryAsync(long libraryId, User user, CancellationToken ct = default);
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
