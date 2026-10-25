using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 媒体库管理服务：库的创建/更新/删除，以及对当前用户可见的库与条目查询。
/// 采用"全局库 + 路径过滤"模型：库由管理员定义并全局共享元数据，
/// 普通用户仅能访问其 <see cref="User.BasePath"/> 覆盖范围内的库与条目。
/// </summary>
public interface IMediaLibraryService
{
    /// <summary>创建媒体库（需要媒体库管理权限）。</summary>
    Task<Resp<MediaLibrary>> CreateAsync(CreateMediaLibraryReq req, User user, CancellationToken ct = default);
    /// <summary>更新媒体库（需要媒体库管理权限）。</summary>
    Task<Resp> UpdateAsync(long id, UpdateMediaLibraryReq req, User user, CancellationToken ct = default);
    /// <summary>删除媒体库及其全部条目/文件/关联（需要媒体库管理权限）。个人状态一并清理。</summary>
    Task<Resp> DeleteAsync(long id, User user, CancellationToken ct = default);
    /// <summary>列出当前用户可见的媒体库（按 BasePath 过滤）。</summary>
    Task<Resp<List<MediaLibrary>>> ListAsync(User user, CancellationToken ct = default);
    /// <summary>获取单个媒体库（校验当前用户可见性）。</summary>
    Task<Resp<MediaLibrary>> GetAsync(long id, User user, CancellationToken ct = default);
    /// <summary>分页查询库内媒体条目（校验可见性；可按状态/关键字过滤）。</summary>
    Task<Resp<PageResp<MediaItem>>> ListItemsAsync(MediaItemQuery query, User user, CancellationToken ct = default);
    /// <summary>获取单个媒体条目（校验可见性）。</summary>
    Task<Resp<MediaItem>> GetItemAsync(long itemId, User user, CancellationToken ct = default);
    /// <summary>列出条目下的媒体文件。</summary>
    Task<Resp<List<MediaFile>>> ListItemFilesAsync(long itemId, User user, CancellationToken ct = default);
    /// <summary>校验指定用户对某媒体库路径是否可见。</summary>
    bool CanSeeLibrary(User user, MediaLibrary library);
    /// <summary>校验指定用户对某媒体条目是否可见（库可见 + 条目路径在用户根目录范围内）。</summary>
    bool CanSeeItem(User user, MediaItem item);
}

/// <summary>创建媒体库请求。</summary>
public sealed class CreateMediaLibraryReq
{
    /// <summary>媒体库名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>媒体库根路径（VFS 绝对路径）。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>媒体库类型。</summary>
    public MediaLibraryType Type { get; set; } = MediaLibraryType.Mixed;
    /// <summary>是否参与定时增量扫描。</summary>
    [JsonPropertyName("auto_scan")]
    public bool AutoScan { get; set; } = true;
    /// <summary>刮削语言（可选）。</summary>
    [JsonPropertyName("metadata_language")]
    public string? MetadataLanguage { get; set; }
}

/// <summary>更新媒体库请求。字段为 null 表示不修改。</summary>
public sealed class UpdateMediaLibraryReq
{
    /// <summary>新名称。</summary>
    public string? Name { get; set; }
    /// <summary>新类型。</summary>
    public MediaLibraryType? Type { get; set; }
    /// <summary>是否参与定时增量扫描。</summary>
    [JsonPropertyName("auto_scan")]
    public bool? AutoScan { get; set; }
    /// <summary>刮削语言。</summary>
    [JsonPropertyName("metadata_language")]
    public string? MetadataLanguage { get; set; }
}

/// <summary>媒体条目分页查询。</summary>
public sealed class MediaItemQuery
{
    /// <summary>媒体库 id。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
    /// <summary>页码，从 1 开始。</summary>
    public int Page { get; set; } = 1;
    /// <summary>每页条数。</summary>
    [JsonPropertyName("per_page")]
    public int PerPage { get; set; } = 30;
    /// <summary>按刮削状态过滤，可为空（全部）。</summary>
    public MediaItemStatus? Status { get; set; }
    /// <summary>标题关键字，可为空。</summary>
    public string? Keyword { get; set; }
}
