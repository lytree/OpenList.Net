using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services.OfflineDownload;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Manages offline download tools and tasks.
/// Supports aria2 and qBittorrent as download backends.
/// </summary>
public class OfflineDownloadService
{
    private readonly ConcurrentDictionary<string, IOfflineDownloadTool> _tools = new();
    private readonly ISettingService _settings;
    private readonly ITaskService _taskService;
    private readonly IConfiguration _config;

    public OfflineDownloadService(ISettingService settings, ITaskService taskService, IConfiguration config)
    {
        _settings = settings;
        _taskService = taskService;
        _config = config;
    }

    /// <summary>Get available download tool names</summary>
    public List<string> GetToolNames() => ["aria2", "qBittorrent", "SimpleHttp"];

    /// <summary>Get or initialize a download tool</summary>
    public async Task<IOfflineDownloadTool?> GetToolAsync(string name, CancellationToken ct = default)
    {
        if (_tools.TryGetValue(name, out var tool))
            return tool.IsReady ? tool : null;

        tool = name.ToLowerInvariant() switch
        {
            "aria2" => CreateAria2Tool(),
            "qbittorrent" => CreateQBittorrentTool(),
            "simplehttp" => new SimpleHttpTool(),
            _ => null
        };

        if (tool == null) return null;

        var result = await tool.InitAsync(ct);
        if (!tool.IsReady) return null;

        _tools[name] = tool;
        return tool;
    }

    /// <summary>Re-initialize a tool (after settings change)</summary>
    public async Task<string?> ReinitToolAsync(string name, CancellationToken ct = default)
    {
        _tools.TryRemove(name, out _);
        IOfflineDownloadTool? tool = name.ToLowerInvariant() switch
        {
            "aria2" => CreateAria2Tool(),
            "qbittorrent" => CreateQBittorrentTool(),
            "simplehttp" => new SimpleHttpTool(),
            _ => null
        };

        if (tool == null) return $"Unknown tool: {name}";
        var result = await tool.InitAsync(ct);
        if (tool.IsReady)
        {
            _tools[name] = tool;
        }
        return result;
    }

    /// <summary>Add URLs for offline download</summary>
    public async Task<List<TaskInfo>> AddUrlsAsync(List<string> urls, string dstDir, string toolName, string deletePolicy = "delete_on_upload_succeed", CancellationToken ct = default)
    {
        var tool = await GetToolAsync(toolName, ct)
            ?? throw new InvalidOperationException($"Download tool '{toolName}' is not available. Please configure it first.");

        var tempDir = Path.Combine(_config["TempDir"] ?? "data/temp", toolName);
        Directory.CreateDirectory(tempDir);

        var tasks = new List<TaskInfo>();
        foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()))
        {
            var task = await _taskService.CreateTaskAsync("offline_download",
                $"Download {url} to {dstDir}",
                async (taskId, innerCt) =>
                {
                    var taskTempDir = Path.Combine(tempDir, taskId);
                    Directory.CreateDirectory(taskTempDir);
                    try
                    {
                        var gid = await tool.AddUrlAsync(new AddUrlArgs
                        {
                            Url = url,
                            TempDir = taskTempDir,
                            TaskId = taskId,
                            DeletePolicy = deletePolicy
                        }, innerCt);

                        // Poll for completion
                        while (!innerCt.IsCancellationRequested)
                        {
                            await Task.Delay(3000, innerCt);
                            try
                            {
                                var status = await tool.GetStatusAsync(gid, innerCt);

                                // Update progress
                                await _taskService.UpdateProgressAsync(taskId, status.Progress, innerCt);

                                // Handle GID change (torrent follow)
                                if (!string.IsNullOrEmpty(status.NewGID))
                                    gid = status.NewGID;

                                if (status.Completed)
                                    return;

                                if (status.Error != null)
                                    throw new InvalidOperationException(status.Error);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { /* retry on transient errors */ }
                        }
                    }
                    finally
                    {
                        try { if (Directory.Exists(taskTempDir)) Directory.Delete(taskTempDir, true); } catch { }
                    }
                }, ct);

            tasks.Add(task);
        }

        return tasks;
    }

    private Aria2Tool CreateAria2Tool()
    {
        var uri = _settings.GetString("aria2_uri") ?? "http://localhost:6800/jsonrpc";
        var secret = _settings.GetString("aria2_secret") ?? "";
        return new Aria2Tool(uri, secret);
    }

    private QBittorrentTool CreateQBittorrentTool()
    {
        var url = _settings.GetString("qbittorrent_url") ?? "http://localhost:8080/";
        var seedTime = int.TryParse(_settings.GetString("qbittorrent_seedtime"), out var st) ? st : 0;

        var username = _settings.GetString("qbittorrent_username") ?? "admin";
        var password = _settings.GetString("qbittorrent_password") ?? "adminadmin";

        return new QBittorrentTool(url, username, password, seedTime);
    }
}
