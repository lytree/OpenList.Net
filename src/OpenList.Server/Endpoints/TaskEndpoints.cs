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

        // Go: AuthNotGuest - guest users cannot access task endpoints
        group.MapGet("undone", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, string? type = null, CancellationToken ct = default) =>
        {
            var userId = GetUserId(user);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            if (u != null && u.IsGuest) return Results.Json(Resp<object>.Error(403, "You are a guest"));
            var isAdmin = user.IsInRole("2");
            var result = await tasks.ListUndoneTasksAsync(type, ct);
            if (!isAdmin && userId.HasValue)
                result = result.Where(t => t.CreatorId == userId.Value).ToList();
            return Results.Json(new Resp<List<TaskInfo>>(result));
        });

        group.MapGet("done", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, string? type = null, CancellationToken ct = default) =>
        {
            var userId = GetUserId(user);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            if (u != null && u.IsGuest) return Results.Json(Resp<object>.Error(403, "You are a guest"));
            var isAdmin = user.IsInRole("2");
            var result = await tasks.ListDoneTasksAsync(type, ct);
            if (!isAdmin && userId.HasValue)
                result = result.Where(t => t.CreatorId == userId.Value).ToList();
            return Results.Json(new Resp<List<TaskInfo>>(result));
        });

        // Go uses query param "tid" for these endpoints, with permission check (admin or own task)
        group.MapPost("info", async (ITaskService tasks, ClaimsPrincipal user, string tid, CancellationToken ct = default) =>
        {
            var task = await tasks.GetTaskAsync(tid, ct);
            if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            if (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value))
                return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
            return Results.Json(new Resp<TaskInfo>(task));
        });

        group.MapPost("cancel", async (ITaskService tasks, ClaimsPrincipal user, string tid, CancellationToken ct = default) =>
        {
            var task = await tasks.GetTaskAsync(tid, ct);
            if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            if (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value))
                return Results.Json(Resp<object>.Error(404, "task not found"));
            await tasks.CancelTaskAsync(tid, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("delete", async (ITaskService tasks, ClaimsPrincipal user, string tid, CancellationToken ct = default) =>
        {
            var task = await tasks.GetTaskAsync(tid, ct);
            if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            if (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value))
                return Results.Json(Resp<object>.Error(404, "task not found"));
            await tasks.DeleteTaskAsync(tid, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("retry", async (ITaskService tasks, ClaimsPrincipal user, string tid, CancellationToken ct = default) =>
        {
            var task = await tasks.GetTaskAsync(tid, ct);
            if (task == null) return Results.Json(Resp<object>.Error(404, "task not found"));
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            if (!isAdmin && (!userId.HasValue || task.CreatorId != userId.Value))
                return Results.Json(Resp<object>.Error(404, "task not found"));
            await tasks.RetryTaskAsync(tid, ct);
            return Results.Json(new Resp<object>(null));
        });

        // Batch operations (Go: body is []string directly, not {"ids":[...]})
        group.MapPost("cancel_some", async (ITaskService tasks, List<string> tids, CancellationToken ct = default) =>
        {
            await tasks.CancelSomeAsync(tids, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("delete_some", async (ITaskService tasks, List<string> tids, CancellationToken ct = default) =>
        {
            await tasks.DeleteSomeAsync(tids, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("retry_some", async (ITaskService tasks, List<string> tids, CancellationToken ct = default) =>
        {
            await tasks.RetrySomeAsync(tids, ct);
            return Results.Json(new Resp<object>(null));
        });

        // Clear operations (Go: clear_done, clear_succeeded, retry_failed - admin sees all, user sees own)
        group.MapPost("clear_done", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, string? type = null, CancellationToken ct = default) =>
        {
            var userId = GetUserId(user);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            if (u != null && u.IsGuest) return Results.Json(Resp<object>.Error(403, "You are a guest"));
            var isAdmin = user.IsInRole("2");
            await tasks.ClearDoneAsync(isAdmin ? null : userId, type, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("clear_succeeded", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, string? type = null, CancellationToken ct = default) =>
        {
            var userId = GetUserId(user);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            if (u != null && u.IsGuest) return Results.Json(Resp<object>.Error(403, "You are a guest"));
            var isAdmin = user.IsInRole("2");
            await tasks.ClearSucceededAsync(isAdmin ? null : userId, type, ct);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("retry_failed", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, string? type = null, CancellationToken ct = default) =>
        {
            var userId = GetUserId(user);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            if (u != null && u.IsGuest) return Results.Json(Resp<object>.Error(403, "You are a guest"));
            var isAdmin = user.IsInRole("2");
            await tasks.RetryFailedAsync(isAdmin ? null : userId, type, ct);
            return Results.Json(new Resp<object>(null));
        });

        // Legacy compatibility: also expose list/info/cancel/retry/delete at top level
        group.MapGet("list", async (ITaskService tasks, ClaimsPrincipal user, string? type = null, CancellationToken ct = default) =>
        {
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            var result = await tasks.ListTasksAsync(type, ct);
            if (!isAdmin && userId.HasValue)
                result = result.Where(t => t.CreatorId == userId.Value).ToList();
            return Results.Json(new Resp<List<TaskInfo>>(result));
        });

        group.MapGet("info_legacy", async (ITaskService tasks, string id, CancellationToken ct = default) =>
        {
            var task = await tasks.GetTaskAsync(id, ct);
            if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
            return Results.Json(new Resp<TaskInfo>(task));
        });

        // Per-type task routes (Go: /task/upload, /task/copy, /task/move, etc.)
        var taskTypes = new[] { "upload", "copy", "move", "offline_download", "offline_download_transfer", "decompress", "decompress_upload" };
        foreach (var taskType in taskTypes)
        {
            var subGroup = group.MapGroup(taskType);

            subGroup.MapGet("undone", async (ITaskService tasks, CancellationToken ct = default) =>
                Results.Json(new Resp<List<TaskInfo>>(await tasks.ListUndoneTasksAsync(taskType, ct))));

            subGroup.MapGet("done", async (ITaskService tasks, CancellationToken ct = default) =>
                Results.Json(new Resp<List<TaskInfo>>(await tasks.ListDoneTasksAsync(taskType, ct))));

            subGroup.MapPost("info", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                var task = await tasks.GetTaskAsync(tid, ct);
                if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
                return Results.Json(new Resp<TaskInfo>(task));
            });

            subGroup.MapPost("cancel", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                await tasks.CancelTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("delete", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                await tasks.DeleteTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry", async (ITaskService tasks, string tid, CancellationToken ct = default) =>
            {
                await tasks.RetryTaskAsync(tid, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("cancel_some", async (ITaskService tasks, List<string> tids, CancellationToken ct = default) =>
            {
                await tasks.CancelSomeAsync(tids, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("delete_some", async (ITaskService tasks, List<string> tids, CancellationToken ct = default) =>
            {
                await tasks.DeleteSomeAsync(tids, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry_some", async (ITaskService tasks, List<string> tids, CancellationToken ct = default) =>
            {
                await tasks.RetrySomeAsync(tids, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("clear_done", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, CancellationToken ct = default) =>
            {
                var userId = GetUserId(user);
                var isAdmin = user.IsInRole("2");
                await tasks.ClearDoneAsync(isAdmin ? null : userId, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("clear_succeeded", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, CancellationToken ct = default) =>
            {
                var userId = GetUserId(user);
                var isAdmin = user.IsInRole("2");
                await tasks.ClearSucceededAsync(isAdmin ? null : userId, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });

            subGroup.MapPost("retry_failed", async (ITaskService tasks, IUserService users, ClaimsPrincipal user, CancellationToken ct = default) =>
            {
                var userId = GetUserId(user);
                var isAdmin = user.IsInRole("2");
                await tasks.RetryFailedAsync(isAdmin ? null : userId, taskType, ct);
                return Results.Json(new Resp<object>(null));
            });
        }

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }
}
