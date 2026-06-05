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

        group.MapGet("list", async (ITaskService tasks, string? type = null) =>
            Results.Json(new Resp<List<TaskInfo>>(await tasks.ListTasksAsync(type))));

        group.MapGet("info", async (ITaskService tasks, string id) =>
        {
            var task = await tasks.GetTaskAsync(id);
            if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
            return Results.Json(new Resp<TaskInfo>(task));
        });

        group.MapPost("cancel", async (ITaskService tasks, TaskIdReq req) =>
        {
            await tasks.CancelTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("retry", async (ITaskService tasks, TaskIdReq req) =>
        {
            await tasks.RetryTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("delete", async (ITaskService tasks, TaskIdReq req) =>
        {
            await tasks.DeleteTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        return endpoints;
    }
}
