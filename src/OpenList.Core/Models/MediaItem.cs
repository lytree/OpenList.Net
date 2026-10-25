using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 媒体条目的刮削匹配状态。
/// </summary>
public enum MediaItemStatus : int
{
    /// <summary>待刮削（扫描发现，尚未匹配元数据）。</summary>
    Pending = 0,
    /// <summary>已匹配（已有元数据 provider 的确认结果）。</summary>
    Matched = 1,
    /// <summary>未匹配（刮削过但没有可靠结果，可人工修正）。</summary>
    Unmatched = 2,
    /// <summary>已忽略（用户标记不再刮削）。</summary>
    Ignored = 3,
}

/// <summary>
/// 媒体条目（一部电影或一部剧集），映射数据库 x_media_items 表。
/// 元数据全局共享：同一媒体库内所有用户看到同一份刮削结果，
/// 个人收藏/进度等状态存于 <see cref="UserMediaState"/>。
/// </summary>
public sealed class MediaItem
{
    /// <summary>媒体条目主键。</summary>
    public long Id { get; set; }
    /// <summary>所属媒体库 id。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
    /// <summary>条目类型（与 <see cref="MediaLibraryType"/> 对齐，Mixed 库在扫描时判定）。</summary>
    public MediaLibraryType Type { get; set; }
    /// <summary>标题（刮削后为规范标题）。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>原始标题（原文名），可为空。</summary>
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }
    /// <summary>发行年份，可为空。</summary>
    public int? Year { get; set; }
    /// <summary>TMDB id（字符串形态存储，便于后续兼容其他 provider）。</summary>
    [JsonPropertyName("tmdb_id")]
    public string? TmdbId { get; set; }
    /// <summary>简介。</summary>
    public string? Overview { get; set; }
    /// <summary>海报路径（VFS 内写回的 poster 文件路径，或外部 URL）。</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    /// <summary>背景图路径（fanart/backdrop）。</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }
    /// <summary>源站评分（0-10），可为空。</summary>
    public double? Rating { get; set; }
    /// <summary>类型标签（JSON 数组字符串，如 ["剧情","科幻"]）。</summary>
    [JsonPropertyName("genres")]
    public string? GenresJson { get; set; }
    /// <summary>刮削匹配状态。</summary>
    public MediaItemStatus Status { get; set; } = MediaItemStatus.Pending;
    /// <summary>条目根目录（VFS 标准化绝对路径）。同一库内唯一。</summary>
    [JsonPropertyName("directory_path")]
    public string DirectoryPath { get; set; } = string.Empty;
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 媒体文件（条目下的具体视频文件），映射数据库 x_media_files 表。
/// 一个条目可含多个文件（多版本、多集）。
/// </summary>
public sealed class MediaFile
{
    /// <summary>媒体文件主键。</summary>
    public long Id { get; set; }
    /// <summary>所属媒体条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>文件 VFS 标准化绝对路径（全局唯一，用于增量扫描判定）。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>文件大小（字节）。</summary>
    public long Size { get; set; }
    /// <summary>时长（秒），可为空（未探测）。</summary>
    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; set; }
    /// <summary>分辨率描述（如 "1920x1080"），可为空（未探测）。</summary>
    public string? Resolution { get; set; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
