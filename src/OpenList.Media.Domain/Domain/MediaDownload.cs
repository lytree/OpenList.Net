using System.Text.Json.Serialization;

namespace OpenList.Media.Domain;

/// <summary>想看条目的资源下载状态。</summary>
public enum MediaDownloadStatus : int
{
    /// <summary>已提交，等待下载器接受。</summary>
    Pending = 0,
    /// <summary>下载中。</summary>
    Downloading = 1,
    /// <summary>下载完成，等待（或已经）扫描入库。</summary>
    Completed = 2,
    /// <summary>下载失败（下载器报错 / 超时）。</summary>
    Failed = 3,
    /// <summary>用户取消。</summary>
    Cancelled = 4,
}

/// <summary>
/// 一次「为库外想看作品找资源并下载」的记录，映射数据库 x_media_downloads 表。
/// <para>
/// 这是 OpenList 离线下载底座（<c>IOfflineDownloadTool</c>：aria2 / qBittorrent / Transmission）
/// 与媒体域的唯一接缝：本行记住「哪个条目、用哪个下载器、下载器那边的任务 id、落到哪个目录」，
/// 后台轮询任务据此把完成的事件翻译成「对该目录做一次局部扫描」。
/// </para>
/// <para>
/// 不给 <see cref="MediaItem"/> 加下载列：一个条目可能先后试多个资源（第一个没速度就换一个），
/// 下载记录天然是一对多的流水。
/// </para>
/// </summary>
public sealed class MediaDownload
{
    /// <summary>主键。</summary>
    public long Id { get; set; }
    /// <summary>目标想看条目（库外作品）id。</summary>
    [JsonPropertyName("want_id")]
    public long WantId { get; set; }
    /// <summary>入库后的条目 id；为空 = 还没入库。</summary>
    [JsonPropertyName("item_id")]
    public long? ItemId { get; set; }
    /// <summary>目标媒体库 id（下载完成后对它做局部扫描）。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }
    /// <summary>下载器名（<c>aria2</c> / <c>qbit</c> / <c>transmission</c> / <c>simple_http</c>）。</summary>
    public string Tool { get; set; } = string.Empty;
    /// <summary>下载器那边的任务 id（aria2 gid / qbit hash），为空表示还没被接受。</summary>
    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }
    /// <summary>资源标题（搜索结果里的原始标题）。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>提交给下载器的地址（磁力 / 种子 / HTTP）。</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>下载器落盘目录（VFS 路径 = 库目录 + 命名规则）。</summary>
    [JsonPropertyName("target_directory")]
    public string TargetDirectory { get; set; } = string.Empty;
    /// <summary>状态。</summary>
    public MediaDownloadStatus Status { get; set; } = MediaDownloadStatus.Pending;
    /// <summary>进度 0–100。</summary>
    public double Progress { get; set; }
    /// <summary>最近一次错误。</summary>
    public string? Error { get; set; }
    /// <summary>提交时间。</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>最近一次状态更新时间。</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>下载完成时间。</summary>
    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>入库扫描完成时间；为空表示下载完成后还没扫过。</summary>
    [JsonPropertyName("ingested_at")]
    public DateTimeOffset? IngestedAt { get; set; }
}
