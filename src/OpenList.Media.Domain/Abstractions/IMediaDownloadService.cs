using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 「想看 → 找资源 → 下载 → 入库」的最后一段：把资源地址交给离线下载底座，
/// 记下下载器那边的任务 id，轮询状态并在产物落进库目录后触发一次扫描。
/// <para>
/// 这一层是媒体域与离线下载（<c>IOfflineDownloadService</c>）之间**唯一**的接缝：
/// 媒体域不该知道 aria2/qBittorrent 的差异，离线下载也不该知道「想看」是什么。
/// </para>
/// <para>
/// 状态口径（与 <see cref="MediaDownloadStatus"/> 一致）：
/// <c>Pending</c> 已提交 → <c>Downloading</c> 下载中 → <c>Completed</c> 下载完成（可能还没入库，
/// 看 <see cref="MediaDownload.IngestedAt"/>）→ <c>Failed</c> / <c>Cancelled</c>。
/// 注意离线下载是两段任务（下载 → 转移到库目录），本层只看第一段；「真的入库了」
/// 由 <see cref="IMediaWantService.LinkIngestedAsync"/> 在扫描后确认。
/// </para>
/// </summary>
public interface IMediaDownloadService
{
    /// <summary>
    /// 提交一次下载：目标是某个想看作品，落到某个媒体库目录下。
    /// 作品已入库、地址非法、库不可写都会在提交前被拒。
    /// </summary>
    Task<Resp<MediaDownload>> SubmitAsync(SubmitMediaDownloadReq req, User user, CancellationToken ct = default);

    /// <summary>列出下载记录（<paramref name="wantId"/> 可限定某条想看），最近的在前。</summary>
    Task<Resp<List<MediaDownload>>> ListAsync(long? wantId, User user, CancellationToken ct = default);

    /// <summary>取消一次下载（幂等）：下载器那边的任务一并取消，终态的行不再动。</summary>
    Task<Resp> CancelAsync(long id, User user, CancellationToken ct = default);

    /// <summary>
    /// 轮询未完成的行，把下载器的任务状态映射回下载记录；下载完成的行触发一次库扫描，
    /// 并把已入库的作品回填到想看行（幂等，宿主服务定时调用）。
    /// </summary>
    /// <returns>本轮状态发生变化的行数。</returns>
    Task<int> RefreshAsync(CancellationToken ct = default);
}

/// <summary>提交下载的请求。</summary>
public sealed class SubmitMediaDownloadReq
{
    /// <summary>目标想看作品 id。</summary>
    [JsonPropertyName("want_id")]
    public long WantId { get; set; }

    /// <summary>产物落到哪个媒体库（必须已存在且当前用户可见）。</summary>
    [JsonPropertyName("library_id")]
    public long LibraryId { get; set; }

    /// <summary>资源地址：磁力、种子直链或 http(s)。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>下载器：<c>aria2</c> / <c>qbit</c> / <c>transmission</c>；空 = SimpleHttp。</summary>
    public string? Tool { get; set; }
}
