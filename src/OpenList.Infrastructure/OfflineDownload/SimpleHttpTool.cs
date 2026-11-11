using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.OfflineDownload;

/// <summary>
/// 服务端自带的直连 HTTP(S) 下载器（无需任何配置，因此永远可用）。
/// <para>
/// 对应 Go 参考实现里「SimpleHttp」这一档：只吃 http/https 直链，不支持 BT/磁力/种子。
/// 下载到本机临时目录后由 <c>OfflineDownloadService</c> 的 transfer 阶段搬运进目标存储；
/// <c>delete_policy = UploadDownloadStream</c> 时该服务会绕过本工具，直接边下边传。
/// </para>
/// <para>
/// 实例是有状态的：每次任务由工厂新建一个，<see cref="AddDownloadAsync"/> 立即返回 id、
/// 后台线程负责实际下载，状态通过 <see cref="GetStatusAsync"/> 轮询获取。
/// </para>
/// </summary>
public sealed class SimpleHttpTool : IOfflineDownloadTool
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// 创建直连下载器实例。
    /// </summary>
    /// <param name="http">HTTP 客户端（大文件下载需要关掉整体超时）。</param>
    public SimpleHttpTool(HttpClient http)
    {
        _http = http;
        // 大文件下载可能远超默认的 100s 超时；传输本身由调用方的取消令牌控制。
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <inheritdoc/>
    public string Name => "SimpleHttp";
    /// <inheritdoc/>
    public bool Enabled => true;

    /// <summary>
    /// 开始一个直链下载。立即返回任务 id，实际下载在后台进行。
    /// </summary>
    /// <param name="url">http/https 直链。</param>
    /// <param name="downloadDir">本机临时目录（由服务端提供的绝对路径）。</param>
    /// <param name="ct">取消令牌（仅用于提交阶段）。</param>
    public Task<Resp<string>> AddDownloadAsync(string url, string downloadDir, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Task.FromResult(Resp<string>.Fail("SimpleHttp only supports http/https urls", RespCodes.BadRequest));

        Directory.CreateDirectory(downloadDir);

        var id = Guid.NewGuid().ToString("N");
        var job = new Job { Url = url, Dir = downloadDir };
        lock (_lock) _jobs[id] = job;

        // 不等待：服务端随后按 GetStatusAsync 轮询进度，与 aria2/qbit 这类异步工具保持同一形态。
        job.Worker = Task.Run(() => RunAsync(job));
        return Task.FromResult(Resp<string>.Ok(id));
    }

    /// <summary>
    /// 取消下载：中止流读取，并清理已写入的半成品（<c>.part</c>）。
    /// </summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<Resp> CancelAsync(string taskId, CancellationToken ct = default)
    {
        Job? job;
        lock (_lock) job = _jobs.TryGetValue(taskId, out var j) ? j : null;
        if (job is null) return Task.FromResult(Resp.Fail("task not found", RespCodes.NotFound));

        try { job.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已结束 —— 无可取消 */ }
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 查询下载进度与产物路径。
    /// </summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<Resp<OfflineDownloadStatus>> GetStatusAsync(string taskId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(taskId, out var job))
                return Task.FromResult(Resp<OfflineDownloadStatus>.Fail("task not found", RespCodes.NotFound));

            var st = new OfflineDownloadStatus
            {
                State = job.State,
                Downloaded = job.Downloaded,
                Total = job.Total,
                Error = job.Error,
            };
            // 只在完成时给出产物路径：Debug/半成品路径不该被 transfer 阶段读到。
            if (job.State == "complete" && job.FilePath is not null) st.Files.Add(job.FilePath);
            return Task.FromResult(Resp<OfflineDownloadStatus>.Ok(st));
        }
    }

    /// <summary>实际下载流程：GET → 流式落 <c>.part</c> → 完成后重命名。</summary>
    private async Task RunAsync(Job job)
    {
        var partPath = (string?)null;
        try
        {
            using var resp = await _http.GetAsync(job.Url, HttpCompletionOption.ResponseHeadersRead, job.Cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                lock (_lock)
                {
                    job.State = "error";
                    job.Error = $"http {resp.StatusCode}";
                }
                return;
            }

            var total = resp.Content.Headers.ContentLength ?? 0;
            var name = ResolveFileName(resp, job.Url);
            var finalPath = Path.Combine(job.Dir, name);
            partPath = finalPath + ".part";

            lock (_lock)
            {
                job.Total = total;
                job.FilePath = finalPath;
                job.State = "running";
            }

            {
                await using var src = await resp.Content.ReadAsStreamAsync(job.Cts.Token);
                await using var dst = File.Create(partPath);
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, job.Cts.Token)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), job.Cts.Token);
                    downloaded += read;
                    lock (_lock) job.Downloaded = downloaded;
                }
            }

            // 先确保句柄关闭再重命名（Windows 上打开着改名会失败）。
            File.Move(partPath, finalPath, overwrite: true);
            lock (_lock) job.State = "complete";
        }
        catch (OperationCanceledException)
        {
            Cleanup(partPath);
            lock (_lock) job.State = "canceled";
        }
        catch (Exception ex)
        {
            Cleanup(partPath);
            lock (_lock)
            {
                job.State = "error";
                job.Error = ex.Message;
            }
        }
    }

    /// <summary>删除半成品文件（失败无害）。</summary>
    private static void Cleanup(string? partPath)
    {
        if (partPath is null) return;
        try { if (File.Exists(partPath)) File.Delete(partPath); }
        catch { /* 清理失败不影响任务状态 */ }
    }

    /// <summary>
    /// 推断落盘文件名：优先 Content-Disposition，其次 URL 路径最后一段。
    /// 无论哪种来源都只取文件名本身，避免 <c>..</c>/分隔符把文件写到临时目录之外。
    /// </summary>
    private static string ResolveFileName(HttpResponseMessage resp, string url)
    {
        var candidate = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(candidate) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            candidate = uri.AbsolutePath;

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            // URL 里的 %20 之类要解码；解码后仍只保留文件名一段。
            try { candidate = Uri.UnescapeDataString(candidate); }
            catch (UriFormatException) { /* 保持原样 */ }
            var name = Path.GetFileName(candidate);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return "download.bin";
    }

    /// <summary>单个下载任务的内存状态。</summary>
    private sealed class Job
    {
        public required string Url { get; init; }
        public required string Dir { get; init; }
        public string? FilePath { get; set; }
        public string State { get; set; } = "pending";
        public long Downloaded { get; set; }
        public long Total { get; set; }
        public string? Error { get; set; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? Worker { get; set; }
    }
}
