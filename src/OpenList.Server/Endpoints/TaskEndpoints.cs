using System.Security.Claims;
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

        // Per-type task routes (Go: /task/upload, /task/copy, /task/move, etc.)
        // Go SetupTaskRoute only registers per-type sub-routes, no top-level task routes.
        var taskTypes = new[] { "upload", "copy", "move", "offline_download", "offline_download_transfer", "decompress", "decompress_upload" };
        foreach (var taskType in taskTypes)
        {
            var subGroup = group.MapGroup(taskType);

            subGroup.MapGet("undone", async (ITaskService tasks, ClaimsPrincipal user, CancellationToken ct = default) =>
            {
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                var result = await tasks.ListUndoneTasksAsync(taskType, ct);
                if (!isAdmin && userId.HasValue)
                    result = result.Where(t => t.CreatorId == userId.Value).ToList();
                return Results.Json(new Resp<List<TaskInfo>>(result));
            });

            subGroup.MapGet("done", async (ITaskService tasks, ClaimsPrincipal user, CancellationToken ct = default) =>
            {
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                var result = await tasks.ListDoneTasksAsync(taskType, ct);
                if (!isAdmin && userId.HasValue)
                    result = result.Where(t => t.CreatorId == userId.Value).ToList();
                return Results.Json(new Resp<List<TaskInfo>>(result));
            });

            subGroup.MapPost("info", async (ITaskService tasks, ClaimsPrincipal user, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                if (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value))
                    return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
                return Results.Json(new Resp<TaskInfo>(task));
            });

            subGroup.MapPost("cancel", async (ITaskService tasks, ClaimsPrincipal user, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                if (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value))
                    return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.CancelTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("delete", async (ITaskService tasks, ClaimsPrincipal user, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                if (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value))
                    return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.DeleteTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry", async (ITaskService tasks, ClaimsPrincipal user, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                if (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value))
                    return Results.Json(Resp<object>.Error(404, "task not found"));
                await tasks.RetryTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("cancel_some", async (ITaskService tasks, ClaimsPrincipal user, List<string> tids, CancellationToken ct = default) =>
            {
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                var errors = new Dictionary<string, string>();
                foreach (var tid in tids)
                {
                    var task = await tasks.GetTaskAsync(tid, ct);
                    if (task == null || (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value)))
                    {
                        errors[tid] = "task not found";
                        continue;
                    }
                    await tasks.CancelTaskAsync(tid, ct);
                }
                return Results.Json(new Resp<Dictionary<string, string>>(errors));
            });

            subGroup.MapPost("delete_some", async (ITaskService tasks, ClaimsPrincipal user, List<string> tids, CancellationToken ct = default) =>
            {
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                var errors = new Dictionary<string, string>();
                foreach (var tid in tids)
                {
                    var task = await tasks.GetTaskAsync(tid, ct);
                    if (task == null || (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value)))
                    {
                        errors[tid] = "task not found";
                        continue;
                    }
                    await tasks.DeleteTaskAsync(tid, ct);
                }
                return Results.Json(new Resp<Dictionary<string, string>>(errors));
            });

            subGroup.MapPost("retry_some", async (ITaskService tasks, ClaimsPrincipal user, List<string> tids, CancellationToken ct = default) =>
            {
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                var errors = new Dictionary<string, string>();
                foreach (var tid in tids)
                {
                    var task = await tasks.GetTaskAsync(tid, ct);
                    if (task == null || (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value)))
                    {
                        errors[tid] = "task not found";
                        continue;
                    }
                    await tasks.RetryTaskAsync(tid, ct);
                }
                return Results.Json(new Resp<Dictionary<string, string>>(errors));
            });

            subGroup.MapPost("clear_done", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, CancellationToken ct = default) =>
            {
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                await tasks.ClearDoneAsync(isAdmin ? null : userId, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("clear_succeeded", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, CancellationToken ct = default) =>
            {
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                await tasks.ClearSucceededAsync(isAdmin ? null : userId, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry_failed", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, CancellationToken ct = default) =>
            {
                var userId = FsHelpers.GetUserId(user);
                var isAdmin = user.IsInRole("2");
                await tasks.RetryFailedAsync(isAdmin ? null : userId, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });
        }

        return endpoints;
    }

}
