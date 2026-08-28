using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.OfflineDownload;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 离线下载编排服务，通过 aria2 / qBittorrent / Transmission 等工具实现离线下载。
/// 每个 URL 经 <see cref="ITaskService"/> 创建后台跟踪任务，提交给所选工具，
/// 并轮询其状态直至完成（或失败 / 取消）。对应 Go 参考实现 <c>pkg/task/offline_download</c>。
/// </summary>
public sealed class OfflineDownloadService : IOfflineDownloadService
{
    private readonly ITaskService _tasks;
    private readonly Func<IFileSystemService> _fsFactory;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;

    /// <summary>
    /// 创建离线下载编排服务实例。
    /// </summary>
    /// <param name="tasks">任务服务，用于为每个下载创建一个跟踪任务。</param>
    /// <param name="fsFactory">文件系统服务工厂，用于校验目标目录可写。</param>
    /// <param name="scopes">作用域工厂，用于创建数据库作用域以读取工具配置。</param>
    /// <param name="http">HTTP 客户端工厂，用于创建各下载工具所需的客户端。</param>
    public OfflineDownloadService(
        ITaskService tasks,
        Func<IFileSystemService> fsFactory,
        IServiceScopeFactory scopes,
        IHttpClientFactory http)
    {
        _tasks = tasks;
        _fsFactory = fsFactory;
        _scopes = scopes;
        _http = http;
    }

    /// <summary>
    /// 为若干 URL 提交离线下载。按 <paramref name="req"/> 指定的工具与目标目录，
    /// 为每个合法 URL 创建一个后台任务并返回其任务 id 列表。
    /// </summary>
    /// <param name="req">离线下载请求，包含 URL 列表、目标目录与工具名。</param>
    /// <param name="user">发起下载的用户。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>携带各 URL 对应任务 id 列表的响应。</returns>
    public async Task<Resp<OfflineDownloadResp>> AddAsync(OfflineDownloadReq req, User user, CancellationToken ct = default)
    {
        if (req.Urls is null || req.Urls.Count == 0)
            return Resp<OfflineDownloadResp>.Fail("urls is required", RespCodes.BadRequest);
        if (string.IsNullOrWhiteSpace(req.Path))
            return Resp<OfflineDownloadResp>.Fail("path is required", RespCodes.BadRequest);

        var tool = (req.Tool ?? "").Trim().ToLowerInvariant();
        // 根据工具名选择对应的任务类型与其构建工厂（从未知工具返回 null 以兜底）。
        var (taskType, factory) = tool switch
        {
            "aria2" => (TaskType.Aria2Down, (Func<CancellationToken, Task<IOfflineDownloadTool>>)BuildAria2Async),
            "qbit" or "qbittorrent" => (TaskType.QbitDown, BuildQbitAsync),
            "transmission" => (TaskType.TransmissionDown, BuildTransmissionAsync),
            _ => ((TaskType)(-1), (Func<CancellationToken, Task<IOfflineDownloadTool>>)null!),
        };
        if (factory is null)
            return Resp<OfflineDownloadResp>.Fail($"unknown offline download tool: {req.Tool}", RespCodes.BadRequest);

        // 校验目标目录可写，避免在提交任何 URL 前因路径非法而失败。
        var listResp = await _fsFactory().ListAsync(new FsListReq { Path = req.Path, PerPage = 1 }, user, ct);
        if (listResp.Code != 200)
            return Resp<OfflineDownloadResp>.Fail("destination path not found", RespCodes.NotFound);

        var tids = new List<string>();
        // 为每个非空 URL 创建一个后台下载任务，并收集其任务 id。
        foreach (var url in req.Urls)
        {
            var u = url?.Trim();
            if (string.IsNullOrEmpty(u)) continue;

            var capturedUrl = u;
            var capturedFactory = factory;
            var tid = await _tasks.CreateAsync(taskType, async (progress, cancel) =>
            {
                await RunDownloadAsync(capturedFactory, capturedUrl, req.Path, progress, cancel);
            }, ct);
            tids.Add(tid);
        }

        return Resp<OfflineDownloadResp>.Ok(new OfflineDownloadResp { Tasks = tids });
    }

    // 在后台任务中执行单个 URL 的下载：构建工具 → 校验启用 → 提交 → 轮询至完成。
    private async Task RunDownloadAsync(
        Func<CancellationToken, Task<IOfflineDownloadTool>> factory,
        string url, string destPath,
        IProgress<TaskProgress> progress, CancellationToken cancel)
    {
        var tool = await factory(cancel);
        if (!tool.Enabled)
            throw new InvalidOperationException($"offline download tool '{tool.Name}' is not configured");

        var addResp = await tool.AddDownloadAsync(url, destPath, cancel);
        if (addResp.Code != 200 || string.IsNullOrEmpty(addResp.Data))
            throw new InvalidOperationException(addResp.Message);

        var externalId = addResp.Data;
        await PollUntilDoneAsync(tool, externalId, progress, cancel);
    }

    // 以固定间隔轮询下载状态，并向上汇报进度；遇到完成/错误/取消状态时结束。
    private static async Task PollUntilDoneAsync(
        IOfflineDownloadTool tool, string externalId,
        IProgress<TaskProgress> progress, CancellationToken cancel)
    {
        using var period = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();
        while (await period.WaitForNextTickAsync(cancel))
        {
            var stResp = await tool.GetStatusAsync(externalId, cancel);
            if (stResp.Code != 200 || stResp.Data is null)
            {
                // 状态查询暂时失败：继续轮询，但累计超过 30 分钟则视为超时。
                if (sw.Elapsed > TimeSpan.FromMinutes(30))
                    throw new TimeoutException("offline download status polling timed out");
                continue;
            }

            var st = stResp.Data;
            progress.Report(new TaskProgress { Current = st.Downloaded, Total = st.Total });

            switch (st.State)
            {
                case "complete":
                    return;
                case "error":
                    throw new InvalidOperationException(st.Error ?? "offline download failed");
                case "canceled":
                    cancel.ThrowIfCancellationRequested();
                    return;
            }
        }
    }

    // 从设置中读取 aria2 的地址与密钥，构建对应的 Aria2 适配器。
    private async Task<IOfflineDownloadTool> BuildAria2Async(CancellationToken ct)
    {
        var uri = await GetSettingAsync("aria2_uri", "");
        var secret = await GetSettingAsync("aria2_secret", "");
        var client = _http.CreateClient("offline_download_aria2");
        return new Aria2Tool(client, uri, secret);
    }

    // 从设置中读取 qBittorrent 的地址与凭据，构建对应的适配器。
    private async Task<IOfflineDownloadTool> BuildQbitAsync(CancellationToken ct)
    {
        var url = await GetSettingAsync("qbittorrent_url", "");
        var user = await GetSettingAsync("qbittorrent_user", "");
        var pass = await GetSettingAsync("qbittorrent_password", "");
        var client = _http.CreateClient("offline_download_qbit");
        return new QbitTool(client, url, user, pass);
    }

    // 从设置中读取 Transmission 的地址与凭据，构建对应的适配器。
    private async Task<IOfflineDownloadTool> BuildTransmissionAsync(CancellationToken ct)
    {
        var uri = await GetSettingAsync("transmission_uri", "");
        var user = await GetSettingAsync("transmission_user", "");
        var pass = await GetSettingAsync("transmission_password", "");
        var client = _http.CreateClient("offline_download_transmission");
        return new TransmissionTool(client, uri, user, pass);
    }

    // 在独立作用域中读取配置项；读取失败时回退到默认值，避免工具构建失败。
    private async Task<string> GetSettingAsync(string key, string fallback)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            var r = await svc.GetAsync(key);
            return (r.Code == 200 && r.Data is not null && !string.IsNullOrWhiteSpace(r.Data.Value))
                ? r.Data.Value
                : fallback;
        }
        catch { return fallback; }
    }
}
