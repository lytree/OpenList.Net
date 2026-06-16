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
        }).WithSummary("获取设置列表");

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
        }).WithSummary("获取设置项");

        group.MapPost("setting/save", async (ISettingService settings, List<SettingItem> items) =>
        {
            await settings.SaveSettingsAsync(items);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("保存设置");

        group.MapPost("setting/delete", async (ISettingService settings, string key) =>
        {
            await settings.DeleteSettingAsync(key);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("删除设置项");

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
        }).WithSummary("获取默认设置");

        group.MapPost("setting/reset_token", async (ISettingService settings) =>
        {
            var token = await settings.ResetTokenAsync();
            return Results.Json(new Resp<string>(token));
        }).WithSummary("重置 Token");

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
        }).WithSummary("设置 Aria2 配置");

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
        }).WithSummary("设置 qBittorrent 配置");

        group.MapPost("setting/set_transmission", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置 Transmission 配置");

        group.MapPost("setting/set_115", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置 115 配置");

        group.MapPost("setting/set_115_open", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置 115 Open 配置");

        group.MapPost("setting/set_123_pan", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置 123 云盘配置");

        group.MapPost("setting/set_123_open", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置 123 Open 配置");

        group.MapPost("setting/set_pikpak", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置 PikPak 配置");

        group.MapPost("setting/set_thunder", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置迅雷下载配置");

        group.MapPost("setting/set_thunderx", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置迅雷 X 配置");

        group.MapPost("setting/set_thunder_browser", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented"))).WithSummary("设置迅雷浏览器配置");
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
        }).WithSummary("构建搜索索引");

        group.MapPost("index/update", (ISearchService search, IConfiguration config, [FromBody] UpdateIndexReq req) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            var paths = req.Paths ?? ["/"];
            _ = Task.Run(() => search.UpdateIndexAsync(paths.FirstOrDefault() ?? "/", []));
            return Results.Json(new Resp<object>(null));
        }).WithSummary("更新搜索索引");

        group.MapPost("index/stop", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            await search.StopIndexAsync();
            return Results.Json(new Resp<object>(null));
        }).WithSummary("停止索引构建");

        group.MapPost("index/clear", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            await search.ClearIndexAsync();
            return Results.Json(new Resp<object>(null));
        }).WithSummary("清除搜索索引");

        group.MapGet("index/progress", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            return Results.Json(new Resp<IndexProgress>(await search.GetProgressAsync()));
        }).WithSummary("获取索引进度");
    }

    private static void MapMessageAdmin(RouteGroupBuilder group)
    {
        group.MapPost("message/get", async (IMessageService messages, ClaimsPrincipal user) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<List<Message>>.Error(401, "unauthorized"));
            return Results.Json(new Resp<List<Message>>(await messages.GetMessagesAsync(userId.Value)));
        }).WithSummary("获取消息列表");

        group.MapPost("message/send", async (IMessageService messages, [FromBody] SendMessageReq req) =>
        {
            await messages.SendMessageAsync(req.UserId, req.Title, req.Content);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("发送消息");
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
        }).WithSummary("开始存储扫描");
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
        }).WithSummary("停止扫描");
        group.MapGet("scan/progress", (IScanService scan) =>
            Results.Json(new Resp<object>(new { obj_count = scan.ScannedCount, is_done = !scan.IsRunning }))).WithSummary("获取扫描进度");
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
            }).WithSummary("管理员获取未完成任务");

            subGroup.MapGet("done", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                var result = await tasks.ListDoneTasksAsync(taskType, ct);
                return Results.Json(new Resp<List<TaskInfo>>(result));
            }).WithSummary("管理员获取已完成任务");

            subGroup.MapPost("info", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
                return Results.Json(new Resp<TaskInfo>(task));
            }).WithSummary("管理员获取任务详情");

            subGroup.MapPost("cancel", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.CancelTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            }).WithSummary("管理员取消任务");

            subGroup.MapPost("delete", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.DeleteTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            }).WithSummary("管理员删除任务");

            subGroup.MapPost("retry", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.RetryTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            }).WithSummary("管理员重试任务");

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
            }).WithSummary("管理员批量取消任务");

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
            }).WithSummary("管理员批量删除任务");

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
            }).WithSummary("管理员批量重试任务");

            subGroup.MapPost("clear_done", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.ClearDoneAsync(null, taskType, ct);
                return Results.Json(new Resp<object>(null));
            }).WithSummary("管理员清除已完成任务");

            subGroup.MapPost("clear_succeeded", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.ClearSucceededAsync(null, taskType, ct);
                return Results.Json(new Resp<object>(null));
            }).WithSummary("管理员清除成功任务");

            subGroup.MapPost("retry_failed", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.RetryFailedAsync(null, taskType, ct);
                return Results.Json(new Resp<object>(null));
            }).WithSummary("管理员重试失败任务");
        }
    }
}
