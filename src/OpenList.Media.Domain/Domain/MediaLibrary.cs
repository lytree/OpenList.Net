using System.Text.Json.Serialization;

namespace OpenList.Media.Domain;

/// <summary>
/// 媒体库类型。数值需与前端/数据库约定一致。
/// </summary>
public enum MediaLibraryType : int
{
    /// <summary>电影库。</summary>
    Movie = 0,
    /// <summary>剧集库。</summary>
    TvShow = 1,
    /// <summary>混合库（按目录结构自动判定）。</summary>
    Mixed = 2,
}

/// <summary>
/// 媒体库实体，映射数据库 x_media_libraries 表。
/// 媒体库是全局资源：仅管理员（或具有媒体库管理权限位的用户）可创建/删除，
/// 普通用户按各自 <see cref="User.BasePath"/> 过滤后看到子集（全局库 + 路径过滤模型）。
/// </summary>
public sealed class MediaLibrary
{
    /// <summary>媒体库主键。</summary>
    public long Id { get; set; }
    /// <summary>媒体库名称（展示用）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>媒体库根路径（VFS 标准化绝对路径，例如 "/media/movies"）。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>媒体库类型。</summary>
    public MediaLibraryType Type { get; set; } = MediaLibraryType.Mixed;
    /// <summary>是否参与定时增量扫描。</summary>
    [JsonPropertyName("auto_scan")]
    public bool AutoScan { get; set; } = true;
    /// <summary>刮削元数据语言（如 "zh-CN"），为空时使用全局设置。</summary>
    [JsonPropertyName("metadata_language")]
    public string? MetadataLanguage { get; set; }
    /// <summary>创建者用户 id。</summary>
    [JsonPropertyName("created_by")]
    public long CreatedBy { get; set; }
    /// <summary>最近一次扫描完成时间，可为空（从未扫描）。</summary>
    [JsonPropertyName("last_scan_at")]
    public DateTimeOffset? LastScanAt { get; set; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 媒体库扫描历史，映射数据库 x_media_scan_logs 表。
/// 每次扫描（全量/增量）写一行，用于管理界面展示与增量游标排查。
/// </summary>
public sealed class MediaScanLog
{
    /// <summary>扫描记录主键。</summary>
    public long Id { get; set; }
    /// <summary>所属媒体库 id。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
    /// <summary>关联的后台任务 id（<c>ITaskService</c> 任务），可为空（同步小扫描）。</summary>
    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }
    /// <summary>本次新增的媒体文件数。</summary>
    public int Added { get; set; }
    /// <summary>本次更新的媒体文件数。</summary>
    public int Updated { get; set; }
    /// <summary>本次移除的媒体文件数（源文件已不存在）。</summary>
    public int Removed { get; set; }
    /// <summary>错误信息，可为空（成功）。</summary>
    public string? Error { get; set; }
    /// <summary>扫描开始时间。</summary>
    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>扫描结束时间，可为空（进行中）。</summary>
    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }
}
