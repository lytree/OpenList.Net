using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/task").RequireAuthorization();

        // General task endpoints (Go: taskRoute with generic manager)
        group.MapGet("undone", async (ITaskService tasks, string? type = null, CancellationToken ct = default) =>
            Results.Json(new Resp<List<TaskInfo>>(await tasks.ListUndoneTasksAsync(type, ct))));

        group.MapGet("done", async (ITaskService tasks, string? type = null, CancellationToken ct = default) =>
            Results.Json(new Resp<List<TaskInfo>>(await tasks.ListDoneTasksAsync(type, ct))));

        group.MapPost("info", async (ITaskService tasks, TaskIdReq req, CancellationToken ct = default) =>
        {
            var task = await tasks.GetTaskAsync(req.Id, ct);
            if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
            return Results.Json(new Resp<TaskInfo>(task));
        });

        group.MapPost("cancel", async (ITaskService tasks, TaskIdReq req, CancellationToken ct = default) =>
        {
            await tasks.CancelTaskAsync(req.Id, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("delete", async (ITaskService tasks, TaskIdReq req, CancellationToken ct = default) =>
        {
            await tasks.DeleteTaskAsync(req.Id, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("retry", async (ITaskService tasks, TaskIdReq req, CancellationToken ct = default) =>
        {
            await tasks.RetryTaskAsync(req.Id, ct);
            return Results.Json(new Resp<object>(null));
        });

        // Batch operations (Go: cancel_some, delete_some, retry_some)
        group.MapPost("cancel_some", async (ITaskService tasks, TaskIdsReq req, CancellationToken ct = default) =>
        {
            await tasks.CancelSomeAsync(req.Ids ?? [], ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("delete_some", async (ITaskService tasks, TaskIdsReq req, CancellationToken ct = default) =>
        {
            await tasks.DeleteSomeAsync(req.Ids ?? [], ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("retry_some", async (ITaskService tasks, TaskIdsReq req, CancellationToken ct = default) =>
        {
            await tasks.RetrySomeAsync(req.Ids ?? [], ct);
            return Results.Json(new Resp<object>(null));
        });

        // Clear operations (Go: clear_done, clear_succeeded)
        group.MapPost("clear_done", async (ITaskService tasks, string? type = null, CancellationToken ct = default) =>
        {
            await tasks.ClearDoneAsync(type, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("clear_succeeded", async (ITaskService tasks, string? type = null, CancellationToken ct = default) =>
        {
            await tasks.ClearSucceededAsync(type, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("retry_failed", async (ITaskService tasks, string? type = null, CancellationToken ct = default) =>
        {
            await tasks.RetryFailedAsync(type, ct);
            return Results.Json(new Resp<object>(null));
        });

        // Legacy compatibility: also expose list/info/cancel/retry/delete at top level
        group.MapGet("list", async (ITaskService tasks, string? type = null, CancellationToken ct = default) =>
            Results.Json(new Resp<List<TaskInfo>>(await tasks.ListTasksAsync(type, ct))));

        group.MapGet("info_legacy", async (ITaskService tasks, string id, CancellationToken ct = default) =>
        {
            var task = await tasks.GetTaskAsync(id, ct);
            if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
            return Results.Json(new Resp<TaskInfo>(task));
        });

        // Per-type task routes (Go: /task/upload, /task/copy, /task/move, etc.)
        var taskTypes = new[] { "upload", "copy", "move", "offline_download", "offline_download_transfer", "decompress" };
        foreach (var taskType in taskTypes)
        {
            var subGroup = group.MapGroup(taskType);

            subGroup.MapGet("undone", async (ITaskService tasks, CancellationToken ct = default) =>
                Results.Json(new Resp<List<TaskInfo>>(await tasks.ListUndoneTasksAsync(taskType, ct))));

            subGroup.MapGet("done", async (ITaskService tasks, CancellationToken ct = default) =>
                Results.Json(new Resp<List<TaskInfo>>(await tasks.ListDoneTasksAsync(taskType, ct))));

            subGroup.MapPost("info", async (ITaskService tasks, TaskIdReq req, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(req.Id, ct);
                if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
                return Results.Json(new Resp<TaskInfo>(task));
            });

            subGroup.MapPost("cancel", async (ITaskService tasks, TaskIdReq req, CancellationToken ct = default) =>
            {
                await tasks.CancelTaskAsync(req.Id, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("delete", async (ITaskService tasks, TaskIdReq req, CancellationToken ct = default) =>
            {
                await tasks.DeleteTaskAsync(req.Id, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry", async (ITaskService tasks, TaskIdReq req, CancellationToken ct = default) =>
            {
                await tasks.RetryTaskAsync(req.Id, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("cancel_some", async (ITaskService tasks, TaskIdsReq req, CancellationToken ct = default) =>
            {
                await tasks.CancelSomeAsync(req.Ids ?? [], ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("delete_some", async (ITaskService tasks, TaskIdsReq req, CancellationToken ct = default) =>
            {
                await tasks.DeleteSomeAsync(req.Ids ?? [], ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry_some", async (ITaskService tasks, TaskIdsReq req, CancellationToken ct = default) =>
            {
                await tasks.RetrySomeAsync(req.Ids ?? [], ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("clear_done", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.ClearDoneAsync(taskType, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("clear_succeeded", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.ClearSucceededAsync(taskType, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry_failed", async (ITaskService tasks, CancellationToken ct = default) =>
            {
                await tasks.RetryFailedAsync(taskType, ct);
                return Results.Json(new Resp<object>(null));
            });
        }

        return endpoints;
    }
}
