using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 离线下载工具抽象（aria2、qBittorrent、115、pikpak、thunder 等）。
/// 与 OpenList Go 的 <c>pkg/task/offline_download</c> 对应。
/// </summary>
public interface IOfflineDownloadTool
{
    /// <summary>工具名称。</summary>
    string Name { get; }
    /// <summary>是否启用。</summary>
    bool Enabled { get; }
    /// <summary>
    /// 添加一个下载任务并返回任务 id。
    /// <para>
    /// <paramref name="downloadDir"/> 必须是**工具进程能直接访问的本机绝对路径**，
    /// 不是 OpenList 的 VFS 路径。aria2 / qBittorrent / Transmission 都是独立进程，
    /// 它们只会往自己那台机器的磁盘写；把 VFS 路径（如 <c>/115/downloads</c>）当作
    /// <c>dir</c> 传过去，文件会落在工具自己的磁盘上，OpenList 完全看不到 ——
    /// 这也是旧实现"提交成功但目标目录里永远没有文件"的根因。
    /// 实际搬运由 <c>OfflineDownloadService</c> 的 transfer 阶段经 VFS 上传完成。
    /// </para>
    /// </summary>
    Task<Resp<string>> AddDownloadAsync(string url, string downloadDir, CancellationToken ct = default);
    /// <summary>取消一个下载任务。</summary>
    Task<Resp> CancelAsync(string taskId, CancellationToken ct = default);
    /// <summary>获取下载任务的状态。</summary>
    Task<Resp<OfflineDownloadStatus>> GetStatusAsync(string taskId, CancellationToken ct = default);
}

/// <summary>
/// 离线下载任务的状态信息。
/// </summary>
public sealed class OfflineDownloadStatus
{
    /// <summary>状态，默认 "pending"。</summary>
    public string State { get; set; } = "pending";
    /// <summary>已下载字节数。</summary>
    public long Downloaded { get; set; }
    /// <summary>总字节数。</summary>
    public long Total { get; set; }
    /// <summary>错误信息，可为空。</summary>
    public string? Error { get; set; }
    /// <summary>
    /// 下载产物的本机绝对路径（transfer 阶段的搬运源）。
    /// <para>
    /// 仅 <c>State == "complete"</c> 时才有意义；指向目录（BT 多文件任务）时
    /// 由 transfer 阶段递归展开。工具无法提供产物列表时为空。
    /// </para>
    /// </summary>
    public List<string> Files { get; set; } = [];
}
