using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static partial class AdminEndpoints
{
    private static void MapSettingAdmin(RouteGroupBuilder group)
    {
        group.MapGet("setting/list", async (ISettingService settings, int? group = null, string? groups = null) =>
        {
            if (!string.IsNullOrEmpty(groups) || group.HasValue)
            {
                var groupList = !string.IsNullOrEmpty(groups)
                    ? groups.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()
                    : [group!.Value];
                return Results.Json(new Resp<List<SettingItem>>(await settings.ListSettingsByGroupsAsync(groupList)));
            }
            return Results.Json(new Resp<List<SettingItem>>(await settings.ListSettingsAsync()));
        });

        group.MapGet("setting/get", async (ISettingService settings, string? key = null, string? keys = null) =>
        {
            if (!string.IsNullOrEmpty(key))
            {
                var item = await settings.GetSettingItemAsync(key);
                if (item == null) return Results.Json(Resp<SettingItem>.Error(404, "setting not found"));
                return Results.Json(new Resp<SettingItem>(item));
            }
            if (!string.IsNullOrEmpty(keys))
            {
                var keyList = keys.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                var items = await settings.GetSettingsInKeysAsync(keyList);
                return Results.Json(new Resp<List<SettingItem>>(items));
            }
            return Results.Json(Resp<object>.Error(400, "key or keys is required"));
        });

        group.MapPost("setting/save", async (ISettingService settings, List<SettingItem> items) =>
        {
            await settings.SaveSettingsAsync(items);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("setting/delete", async (ISettingService settings, string key) =>
        {
            await settings.DeleteSettingAsync(key);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("setting/default", async (ISettingService settings, int? group = null, string? groups = null) =>
        {
            if (!string.IsNullOrEmpty(groups) || group.HasValue)
            {
                var groupList = !string.IsNullOrEmpty(groups)
                    ? groups.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()
                    : [group!.Value];
                return Results.Json(new Resp<List<SettingItem>>(await settings.DefaultSettingsByGroupsAsync(groupList)));
            }
            return Results.Json(new Resp<List<SettingItem>>(await settings.DefaultSettingsAsync()));
        });

        group.MapPost("setting/reset_token", async (ISettingService settings) =>
        {
            var token = await settings.ResetTokenAsync();
            return Results.Json(new Resp<string>(token));
        });

        group.MapPost("setting/set_aria2", async (OfflineDownloadService downloadService, ISettingService settings, SetToolSettingReq req, CancellationToken ct) =>
        {
            if (req.Data is Dictionary<string, object> dict)
            {
                var items = dict.Select(kv => new SettingItem { Key = $"aria2_{kv.Key}", Value = kv.Value?.ToString() ?? "" }).ToList();
                await settings.SaveSettingsAsync(items, ct);
                var result = await downloadService.ReinitToolAsync("aria2", ct);
                return Results.Json(new Resp<object>(result ?? "ok"));
            }
            return Results.Json(Resp<object>.Error(400, "invalid data"));
        });

        group.MapPost("setting/set_qbit", async (OfflineDownloadService downloadService, ISettingService settings, SetToolSettingReq req, CancellationToken ct) =>
        {
            if (req.Data is Dictionary<string, object> dict)
            {
                var items = dict.Select(kv => new SettingItem { Key = $"qbittorrent_{kv.Key}", Value = kv.Value?.ToString() ?? "" }).ToList();
                await settings.SaveSettingsAsync(items, ct);
                var result = await downloadService.ReinitToolAsync("qBittorrent", ct);
                return Results.Json(new Resp<object>(result ?? "ok"));
            }
            return Results.Json(Resp<object>.Error(400, "invalid data"));
        });

        group.MapPost("setting/set_transmission", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_115", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_115_open", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_123_pan", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_123_open", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_pikpak", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunder", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunderx", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunder_browser", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));
    }

    private static void MapIndexAdmin(RouteGroupBuilder group)
    {
        group.MapPost("index/build", (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            _ = Task.Run(() => search.BuildIndexAsync());
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/update", (ISearchService search, IConfiguration config, [FromBody] UpdateIndexReq req) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            var paths = req.Paths ?? ["/"];
            _ = Task.Run(() => search.UpdateIndexAsync(paths.FirstOrDefault() ?? "/", []));
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/stop", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            await search.StopIndexAsync();
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/clear", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            await search.ClearIndexAsync();
            return Results.Json(new Resp<object>(null));
        });

        group.MapGet("index/progress", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            return Results.Json(new Resp<IndexProgress>(await search.GetProgressAsync()));
        });
    }

    private static void MapMessageAdmin(RouteGroupBuilder group)
    {
        group.MapPost("message/get", async (IMessageService messages, ClaimsPrincipal user) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<List<Message>>.Error(401, "unauthorized"));
            return Results.Json(new Resp<List<Message>>(await messages.GetMessagesAsync(userId.Value)));
        });

        group.MapPost("message/send", async (IMessageService messages, [FromBody] SendMessageReq req) =>
        {
            await messages.SendMessageAsync(req.UserId, req.Title, req.Content);
            return Results.Json(new Resp<object>(null));
        });
    }

    private static void MapScanAdmin(RouteGroupBuilder group)
    {
        group.MapPost("scan/start", async (IScanService scan, ScanReq req, CancellationToken ct) =>
        {
            try
            {
                await scan.StartScanAsync(req.Path ?? "/", req.Limit, ct);
                return Results.Json(new Resp<object>(null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(Resp<object>.Error(400, ex.Message));
            }
        });
        group.MapPost("scan/stop", async (IScanService scan, CancellationToken ct) =>
        {
            try
            {
                await scan.StopScanAsync(ct);
                return Results.Json(new Resp<object>(null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(Resp<object>.Error(400, ex.Message));
            }
        });
        group.MapGet("scan/progress", (IScanService scan) =>
            Results.Json(new Resp<object>(new { obj_count = scan.ScannedCount, is_done = !scan.IsRunning })));
    }

    private static void MapTaskAdmin(RouteGroupBuilder group)
    {
        // Go: _task(g.Group("/task")) → SetupTaskRoute
        // Same per-type structure as user task routes, but admin can see all tasks (no per-user filtering)
        var taskGroup = group.MapGroup("task");
        var taskTypes = new[] { "upload", "copy", "move", "offline_download", "offline_download_transfer", "decompress", "decompress_upload" };
        foreach (var taskType in taskTypes)
        {
            var subGroup = taskGroup.MapGroup(taskType);

            subGroup.MapGet("undone", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                var result = await tasks.ListUndoneTasksAsync(taskType, ct);
                return Results.Json(new Resp<List<TaskInfo>>(result));
            });

            subGroup.MapGet("done", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                var result = await tasks.ListDoneTasksAsync(taskType, ct);
                return Results.Json(new Resp<List<TaskInfo>>(result));
            });

            subGroup.MapPost("info", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
                return Results.Json(new Resp<TaskInfo>(task));
            });

            subGroup.MapPost("cancel", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.CancelTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("delete", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.DeleteTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.RetryTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("cancel_some", async (ITaskService tasks, [FromBody] List<string> tids, CancellationToken ct = default) =>
            {
                var errors = new Dictionary<string, string>();
                foreach (var tid in tids)
                {
                    var task = await tasks.GetTaskAsync(tid, ct);
                    if (task == null) { errors[tid] = "task not found"; continue; }
                    await tasks.CancelTaskAsync(tid, ct);
                }
                return Results.Json(new Resp<Dictionary<string, string>>(errors));
            });

            subGroup.MapPost("delete_some", async (ITaskService tasks, [FromBody] List<string> tids, CancellationToken ct = default) =>
            {
                var errors = new Dictionary<string, string>();
                foreach (var tid in tids)
                {
                    var task = await tasks.GetTaskAsync(tid, ct);
                    if (task == null) { errors[tid] = "task not found"; continue; }
                    await tasks.DeleteTaskAsync(tid, ct);
                }
                return Results.Json(new Resp<Dictionary<string, string>>(errors));
            });

            subGroup.MapPost("retry_some", async (ITaskService tasks, [FromBody] List<string> tids, CancellationToken ct = default) =>
            {
                var errors = new Dictionary<string, string>();
                foreach (var tid in tids)
                {
                    var task = await tasks.GetTaskAsync(tid, ct);
                    if (task == null) { errors[tid] = "task not found"; continue; }
                    await tasks.RetryTaskAsync(tid, ct);
                }
                return Results.Json(new Resp<Dictionary<string, string>>(errors));
            });

            subGroup.MapPost("clear_done", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.ClearDoneAsync(null, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("clear_succeeded", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.ClearSucceededAsync(null, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry_failed", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.RetryFailedAsync(null, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });
        }
    }
}
