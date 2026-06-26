using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.OfflineDownload;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Orchestrates offline downloads via aria2 / qBittorrent / Transmission.
/// Creates a tracking task per URL via <see cref="ITaskService"/>, submits to the
/// tool, and polls status until completion (or failure / cancellation). Mirrors
/// OpenList Go <c>pkg/task/offline_download</c>.
/// </summary>
public sealed class OfflineDownloadService : IOfflineDownloadService
{
    private readonly ITaskService _tasks;
    private readonly IFileSystemService _fs;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;

    public OfflineDownloadService(
        ITaskService tasks,
        IFileSystemService fs,
        IServiceScopeFactory scopes,
        IHttpClientFactory http)
    {
        _tasks = tasks;
        _fs = fs;
        _scopes = scopes;
        _http = http;
    }

    public async Task<Resp<OfflineDownloadResp>> AddAsync(OfflineDownloadReq req, User user, CancellationToken ct = default)
    {
        if (req.Urls is null || req.Urls.Count == 0)
            return Resp<OfflineDownloadResp>.Fail("urls is required", RespCodes.BadRequest);
        if (string.IsNullOrWhiteSpace(req.Path))
            return Resp<OfflineDownloadResp>.Fail("path is required", RespCodes.BadRequest);

        var tool = (req.Tool ?? "").Trim().ToLowerInvariant();
        var (taskType, factory) = tool switch
        {
            "aria2" => (TaskType.Aria2Down, (Func<CancellationToken, Task<IOfflineDownloadTool>>)BuildAria2Async),
            "qbit" or "qbittorrent" => (TaskType.QbitDown, BuildQbitAsync),
            "transmission" => (TaskType.TransmissionDown, BuildTransmissionAsync),
            _ => ((TaskType)(-1), (Func<CancellationToken, Task<IOfflineDownloadTool>>)null!),
        };
        if (factory is null)
            return Resp<OfflineDownloadResp>.Fail($"unknown offline download tool: {req.Tool}", RespCodes.BadRequest);

        // Validate the destination is a writable directory before submitting any URL.
        var listResp = await _fs.ListAsync(new FsListReq { Path = req.Path, PerPage = 1 }, user, ct);
        if (listResp.Code != 200)
            return Resp<OfflineDownloadResp>.Fail("destination path not found", RespCodes.NotFound);

        var tids = new List<string>();
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
                // Transient lookup failure — keep polling but cap total time.
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

    private async Task<IOfflineDownloadTool> BuildAria2Async(CancellationToken ct)
    {
        var uri = await GetSettingAsync("aria2_uri", "");
        var secret = await GetSettingAsync("aria2_secret", "");
        var client = _http.CreateClient("offline_download_aria2");
        return new Aria2Tool(client, uri, secret);
    }

    private async Task<IOfflineDownloadTool> BuildQbitAsync(CancellationToken ct)
    {
        var url = await GetSettingAsync("qbittorrent_url", "");
        var user = await GetSettingAsync("qbittorrent_user", "");
        var pass = await GetSettingAsync("qbittorrent_password", "");
        var client = _http.CreateClient("offline_download_qbit");
        return new QbitTool(client, url, user, pass);
    }

    private async Task<IOfflineDownloadTool> BuildTransmissionAsync(CancellationToken ct)
    {
        var uri = await GetSettingAsync("transmission_uri", "");
        var user = await GetSettingAsync("transmission_user", "");
        var pass = await GetSettingAsync("transmission_password", "");
        var client = _http.CreateClient("offline_download_transmission");
        return new TransmissionTool(client, uri, user, pass);
    }

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
