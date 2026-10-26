using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 播放列表服务。列表是<em>用户私有</em>资源：读写一律校验归属，
/// 不属于当前用户时按"不存在"处理（404），不泄漏他人列表的存在性。
/// 往列表里加条目时还会校验条目对该用户可见。
/// </summary>
public interface IMediaPlaylistService
{
    /// <summary>列出当前用户的全部播放列表（按更新时间倒序，带条目数与预览封面）。</summary>
    Task<Resp<List<MediaPlaylistSummary>>> ListAsync(User user, CancellationToken ct = default);
    /// <summary>获取单个播放列表详情（含按位置排序的条目）。</summary>
    Task<Resp<MediaPlaylistDetail>> GetAsync(long id, User user, CancellationToken ct = default);
    /// <summary>创建播放列表。</summary>
    Task<Resp<MediaPlaylistSummary>> CreateAsync(CreateMediaPlaylistReq req, User user, CancellationToken ct = default);
    /// <summary>重命名 / 改描述。</summary>
    Task<Resp<MediaPlaylistSummary>> UpdateAsync(long id, UpdateMediaPlaylistReq req, User user, CancellationToken ct = default);
    /// <summary>删除播放列表及其成员（不动媒体条目本身）。</summary>
    Task<Resp> DeleteAsync(long id, User user, CancellationToken ct = default);
    /// <summary>把条目追加到列表末尾（已在列表里则不重复添加）。</summary>
    Task<Resp<MediaPlaylistDetail>> AddItemAsync(long id, long itemId, User user, CancellationToken ct = default);
    /// <summary>从列表移除条目，并压实后续位置。</summary>
    Task<Resp<MediaPlaylistDetail>> RemoveItemAsync(long id, long itemId, User user, CancellationToken ct = default);
    /// <summary>按给定顺序重排；未列出的成员保持原有相对顺序排在末尾。</summary>
    Task<Resp<MediaPlaylistDetail>> ReorderAsync(long id, IReadOnlyList<long> itemIds, User user, CancellationToken ct = default);
}

/// <summary>创建播放列表请求。</summary>
public sealed class CreateMediaPlaylistReq
{
    /// <summary>名称（必填，最长 100 字符）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>描述，可为空。</summary>
    public string? Description { get; set; }
}

/// <summary>更新播放列表请求。字段为 null 表示不修改。</summary>
public sealed class UpdateMediaPlaylistReq
{
    /// <summary>新名称。</summary>
    public string? Name { get; set; }
    /// <summary>新描述（传空串即清空）。</summary>
    public string? Description { get; set; }
}

/// <summary>重排请求。</summary>
public sealed class ReorderMediaPlaylistReq
{
    /// <summary>按新顺序排列的条目 id 列表。</summary>
    [JsonPropertyName("item_ids")]
    public List<long> ItemIds { get; set; } = [];
}

/// <summary>播放列表里引用一个条目的最小请求体。</summary>
public sealed class PlaylistItemRef
{
    /// <summary>媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
}

/// <summary>
/// "加入播放列表"便捷请求：给了 <see cref="PlaylistId"/> 就加进那个列表，
/// 否则按 <see cref="Name"/> 新建一个再放进去。
/// </summary>
public sealed class AddToPlaylistReq
{
    /// <summary>媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>目标列表 id，可为空（为空则新建）。</summary>
    [JsonPropertyName("playlist_id")]
    public long? PlaylistId { get; set; }
    /// <summary>新建列表时用的名称，可为空（默认"我的列表"）。</summary>
    public string? Name { get; set; }
}

/// <summary>播放列表列表项（不含全部条目，只带计数与预览）。</summary>
public sealed class MediaPlaylistSummary
{
    /// <summary>列表 id。</summary>
    public long Id { get; set; }
    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>描述。</summary>
    public string? Description { get; set; }
    /// <summary>条目数量。</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }
    /// <summary>前若干个条目 id，供前端拼封面墙（最多 5 个）。</summary>
    [JsonPropertyName("preview_item_ids")]
    public List<long> PreviewItemIds { get; set; } = [];
    /// <summary>创建时间。</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间。</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>播放列表详情（含条目实体）。</summary>
public sealed class MediaPlaylistDetail
{
    /// <summary>列表 id。</summary>
    public long Id { get; set; }
    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>描述。</summary>
    public string? Description { get; set; }
    /// <summary>按位置排序的条目。</summary>
    public List<MediaItem> Items { get; set; } = [];
    /// <summary>创建时间。</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间。</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}
