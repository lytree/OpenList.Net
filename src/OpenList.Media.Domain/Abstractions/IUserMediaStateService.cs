using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 用户媒体状态服务：收藏/看过/评分/播放进度的读写，以及基于状态的口味分析。
/// 所有状态按 (UserId, ItemId) 隔离，替代单用户媒体管理工具的全局状态。
/// </summary>
public interface IUserMediaStateService
{
    /// <summary>获取当前用户对某条目的状态（无记录时返回默认空状态）。</summary>
    Task<Resp<UserMediaState>> GetAsync(long itemId, User user, CancellationToken ct = default);
    /// <summary>更新当前用户对某条目的状态（upsert）。</summary>
    Task<Resp<UserMediaState>> UpdateAsync(long itemId, UpdateUserMediaStateReq req, User user, CancellationToken ct = default);
    /// <summary>分页列出当前用户收藏的条目。</summary>
    Task<Resp<PageResp<MediaItem>>> ListFavoritesAsync(int page, int perPage, User user, CancellationToken ct = default);
    /// <summary>分页列出当前用户"在看"（有进度未看完）的条目。</summary>
    Task<Resp<PageResp<MediaItem>>> ListInProgressAsync(int page, int perPage, User user, CancellationToken ct = default);
    /// <summary>分页列出当前用户"稍后看"队列里的条目。</summary>
    Task<Resp<PageResp<MediaItem>>> ListWatchLaterAsync(int page, int perPage, User user, CancellationToken ct = default);
    /// <summary>分页列出当前用户"已标记"的条目。</summary>
    Task<Resp<PageResp<MediaItem>>> ListFlaggedAsync(int page, int perPage, User user, CancellationToken ct = default);
    /// <summary>
    /// 分页列出当前用户"未看"的条目（没有任何看过记录）。<paramref name="libraryId"/> 为空时跨库。
    /// 这是唯一一条<em>否定式</em>查询——其余状态都基于已有状态行，它基于"没有行"。
    /// </summary>
    Task<Resp<PageResp<MediaItem>>> ListUnseenAsync(int page, int perPage, long? libraryId, User user, CancellationToken ct = default);
    /// <summary>当前用户的口味画像（基于收藏/评分/观看记录统计）。</summary>
    Task<Resp<TasteProfile>> GetTasteProfileAsync(User user, CancellationToken ct = default);
}

/// <summary>更新个人媒体状态请求。字段为 null 表示不修改。</summary>
public sealed class UpdateUserMediaStateReq
{
    /// <summary>收藏。</summary>
    public bool? Favorite { get; set; }
    /// <summary>看过。</summary>
    public bool? Watched { get; set; }
    /// <summary>稍后看。</summary>
    [JsonPropertyName("watch_later")]
    public bool? WatchLater { get; set; }
    /// <summary>标记（待复查/待处理）。</summary>
    [JsonPropertyName("flagged")]
    public bool? Flagged { get; set; }
    /// <summary>标记备注（传空串即清空）。</summary>
    [JsonPropertyName("flag_note")]
    public string? FlagNote { get; set; }
    /// <summary>评分（1-10）。</summary>
    [JsonPropertyName("user_rating")]
    public int? UserRating { get; set; }
    /// <summary>播放进度（秒）。</summary>
    [JsonPropertyName("progress_seconds")]
    public long? ProgressSeconds { get; set; }
}

/// <summary>用户口味画像。</summary>
public sealed class TasteProfile
{
    /// <summary>收藏条目总数。</summary>
    public int FavoriteCount { get; set; }
    /// <summary>已看条目总数。</summary>
    public int WatchedCount { get; set; }
    /// <summary>累计观看时长（秒，按进度累计）。</summary>
    public long TotalWatchSeconds { get; set; }
    /// <summary>类型偏好（按加权次数降序）。</summary>
    public List<TasteGenreWeight> Genres { get; set; } = [];
    /// <summary>偏好的创作者（按出现次数降序，取前 N）。</summary>
    public List<TastePersonWeight> Persons { get; set; } = [];
}

/// <summary>类型偏好权重。</summary>
public sealed class TasteGenreWeight
{
    /// <summary>类型名。</summary>
    public string Genre { get; set; } = string.Empty;
    /// <summary>加权次数（收藏=2，看过=1，评分≥8=2 累加）。</summary>
    public int Weight { get; set; }
}

/// <summary>创作者偏好权重。</summary>
public sealed class TastePersonWeight
{
    /// <summary>创作者 id。</summary>
    public long PersonId { get; set; }
    /// <summary>姓名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>头像路径。</summary>
    public string? ProfilePath { get; set; }
    /// <summary>加权次数。</summary>
    public int Weight { get; set; }
}
