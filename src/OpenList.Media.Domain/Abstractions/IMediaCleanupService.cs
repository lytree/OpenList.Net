using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 媒体库数据清理服务：孤儿记录、重复文件、条目删除与回收站。
/// <para>
/// 四件事共用同一条口径：**先预览再执行由前端的两步按钮负责，后端每个删除接口都幂等**
/// （id 不存在直接跳过，不报错）。原因是清理页天然会「预览 → 停一会 → 执行」，
/// 中间可能有一次扫描把孤儿行顺手清掉了；若执行时报 404，用户只会看到一个莫名其妙的失败。
/// </para>
/// <para>
/// 文件与数据库行分开处理：孤儿记录只删行（文件早就没了）；重复 / 条目删除才碰文件，
/// 且是否走回收站由**条目所属库**的 <c>TrashDirectory</c> 决定。
/// </para>
/// </summary>
public interface IMediaCleanupService
{
    /// <summary>
    /// 列出孤儿条目（条目下全部 <c>MediaFile.Path</c> 在 VFS 上都已不存在）。
    /// <paramref name="libraryId"/> 为 0 时扫描调用者可见的全部库；指定了不可见 / 不存在的库返回 404。
    /// </summary>
    Task<Resp<List<OrphanItem>>> ListOrphansAsync(long libraryId, User user, CancellationToken ct = default);

    /// <summary>
    /// 删除孤儿条目的数据库行（<see cref="MediaItem"/> / <see cref="MediaFile"/> / 关联 / 个人状态 / 观看会话 /
    /// 播放列表成员），**不碰任何文件**。不可见库里的条目 id 直接跳过。
    /// </summary>
    Task<Resp> DeleteOrphansAsync(IReadOnlyList<long> itemIds, User user, CancellationToken ct = default);

    /// <summary>
    /// 列出重复文件组：同库内先按 <c>(Size, DurationSeconds)</c> 初筛，再对每个文件的前 1 MB 算 SHA-256 确认。
    /// 只返回确认后仍有 ≥ 2 个文件的组，按 <see cref="DuplicateGroup.SizeBytes"/> 倒序。
    /// </summary>
    Task<Resp<List<DuplicateGroup>>> ListDuplicatesAsync(long libraryId, User user, CancellationToken ct = default);

    /// <summary>
    /// 删除重复条目：先按回收站规则处理文件，再删数据库行。库配了 <c>TrashDirectory</c> 就移进去，否则直接删。
    /// </summary>
    Task<Resp> DeleteDuplicatesAsync(IReadOnlyList<long> itemIds, User user, CancellationToken ct = default);

    /// <summary>
    /// 删除单个条目。<paramref name="deleteFiles"/> 为 true 时按回收站规则处理文件；为 false 时只删行
    /// （与原 <c>libraries/{id}/delete</c> 的「只删 DB 行」行为一致）。
    /// </summary>
    Task<Resp> DeleteItemAsync(long itemId, bool deleteFiles, User user, CancellationToken ct = default);

    /// <summary>清空指定库回收站目录下的全部内容。库未配置回收站返回 400 <c>trash directory not configured</c>。</summary>
    Task<Resp> EmptyTrashAsync(long libraryId, User user, CancellationToken ct = default);
}

/// <summary>孤儿条目（文件已全部消失、只剩数据库行的条目）。</summary>
public sealed class OrphanItem
{
    /// <summary>条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>条目标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>条目根目录（VFS 绝对路径）。</summary>
    [JsonPropertyName("directory_path")]
    public string DirectoryPath { get; set; } = string.Empty;
    /// <summary>所属库 id。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
    /// <summary>所属库名称（跨库扫描时前端要能标出是哪个库的）。</summary>
    [JsonPropertyName("library_name")]
    public string LibraryName { get; set; } = string.Empty;
    /// <summary>该条目下已不存在的文件数（= 文件行数；条目本来就没有文件行时为 0）。</summary>
    [JsonPropertyName("missing_files")]
    public int MissingFiles { get; set; }
}

/// <summary>一组经哈希确认的重复文件。</summary>
public sealed class DuplicateGroup
{
    /// <summary>组内每个文件的大小（字节）。</summary>
    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }
    /// <summary>组内文件数（同一条目的多版本文件各算一个）。</summary>
    public int Count { get; set; }
    /// <summary>组内条目（去重后按标题排序）。</summary>
    public List<DuplicateItem> Items { get; set; } = [];
}

/// <summary>重复组里的一个条目。</summary>
public sealed class DuplicateItem
{
    /// <summary>条目 id。</summary>
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    /// <summary>条目标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>条目根目录（VFS 绝对路径）。</summary>
    [JsonPropertyName("directory_path")]
    public string DirectoryPath { get; set; } = string.Empty;
    /// <summary>所属库 id。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
    /// <summary>触发命中的那个文件（VFS 绝对路径），前端展示「哪个文件重复」。</summary>
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;
    /// <summary>时长（秒），未探测为 null。</summary>
    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; set; }
    /// <summary>分辨率（如 <c>1920x1080</c>），未探测为 null。</summary>
    public string? Resolution { get; set; }
}

/// <summary>批量删除请求体：<c>{ "item_ids": [1, 2] }</c>。</summary>
public sealed class CleanupItemsReq
{
    /// <summary>要删除的条目 id 列表。</summary>
    [JsonPropertyName("item_ids")]
    public List<long> ItemIds { get; set; } = [];
}

/// <summary>单条删除请求体：<c>{ "delete_files": true }</c>。</summary>
public sealed class CleanupDeleteItemReq
{
    /// <summary>是否连文件一起删（按库的回收站规则处理）。</summary>
    [JsonPropertyName("delete_files")]
    public bool DeleteFiles { get; set; }
}

/// <summary>清空回收站请求体：<c>{ "library_id": 1 }</c>。</summary>
public sealed class CleanupTrashReq
{
    /// <summary>媒体库 id。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
}
