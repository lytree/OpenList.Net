using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 搜索索引服务：构建/更新/清空搜索索引并查询文件。
/// 与 OpenList Go 的 <c>server/handles/search.go</c> 对应。
/// </summary>
public interface ISearchService
{
    /// <summary>全量重建搜索索引。paths 默认为 ["/"]，maxDepth 为 -1 时表示深度不限。</summary>
    Task<Resp> BuildIndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct = default);
    /// <summary>对给定路径做增量更新。</summary>
    Task<Resp> UpdateIndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct = default);
    /// <summary>停止索引构建/更新。</summary>
    Task<Resp> StopIndexAsync(CancellationToken ct = default);
    /// <summary>清空搜索索引。</summary>
    Task<Resp> ClearIndexAsync(CancellationToken ct = default);
    /// <summary>启动时恢复持久化的索引进度（镜像 Go 的 <c>bootstrap.InitIndex</c>）。</summary>
    Task<Resp> InitIndexAsync(CancellationToken ct = default);
    /// <summary>获取索引进度。</summary>
    Task<Resp<IndexProgress>> GetProgressAsync(CancellationToken ct = default);
    /// <summary>执行搜索。</summary>
    Task<Resp<SearchResp>> SearchAsync(SearchReq req, CancellationToken ct = default);
}

/// <summary>
/// 索引构建/更新的进度信息。
/// </summary>
public sealed class IndexProgress
{
    /// <summary>索引状态，默认 "idle"。</summary>
    public string State { get; set; } = "idle";
    /// <summary>已索引的条目数。</summary>
    public long Indexed { get; set; }
    /// <summary>待索引的条目总数。</summary>
    public long Total { get; set; }
    /// <summary>开始时间。</summary>
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>结束时间。</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>上次成功构建/更新完成的时间戳（持久化）。</summary>
    public DateTimeOffset? LastDoneTime { get; set; }
    /// <summary>上次运行的错误消息（无则为空）。</summary>
    public string Error { get; set; } = string.Empty;
}
