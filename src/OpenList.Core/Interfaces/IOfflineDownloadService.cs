using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 通过 aria2 / qBittorrent / Transmission 编排离线下载。
/// 校验目标路径、选择请求的工具、通过 <see cref="ITaskService"/> 创建跟踪任务，
/// 并不断轮询工具状态直至下载完成（或失败/被取消）。与 OpenList Go 的
/// <c>pkg/task/offline_download</c> 对应。
/// </summary>
public interface IOfflineDownloadService
{
    /// <summary>
    /// 将 URL 添加到请求的离线下载工具并开始跟踪。
    /// 返回新建的任务 id 列表。
    /// </summary>
    Task<Resp<OfflineDownloadResp>> AddAsync(OfflineDownloadReq req, User user, CancellationToken ct = default);
}

/// <summary>
/// 离线下载添加结果。
/// </summary>
public sealed class OfflineDownloadResp
{
    /// <summary>创建的任务 id 列表（每个 URL 对应一个）。</summary>
    public List<string> Tasks { get; set; } = [];
}
