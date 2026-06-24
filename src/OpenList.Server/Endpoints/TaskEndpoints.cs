using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Task management endpoints: list done/undone, cancel, delete, clear, retry.
/// Mirrors OpenList Go <c>server/handles/task.go</c>.
/// All endpoints require authentication.
/// </summary>
public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/task").WithTags("Task").RequireAuthorization();

        // GET /api/task/undone?type= — list pending/running tasks
        g.MapGet("/undone", async (HttpContext ctx, ITaskService tasks) =>
        {
            var type = ParseType(ctx.Request.Query["type"]);
            var r = await tasks.UndoneAsync(type, ctx.RequestAborted);
            return Results.Ok(r);
        });

        // GET /api/task/done?type= — list finished tasks (succeeded/failed/canceled)
        g.MapGet("/done", async (HttpContext ctx, ITaskService tasks) =>
        {
            var type = ParseType(ctx.Request.Query["type"]);
            var r = await tasks.DoneAsync(type, ctx.RequestAborted);
            return Results.Ok(r);
        });

        // POST /api/task/info?tid= — get task info
        g.MapGet("/info", async (HttpContext ctx, ITaskService tasks) =>
        {
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            // Try each task type to find the matching task.
            foreach (TaskType type in Enum.GetValues<TaskType>())
            {
                var r = await tasks.InfoAsync(type, tid, ctx.RequestAborted);
                if (r.Code == 200) return Results.Ok(r);
            }
            return Results.Ok(Resp<TaskInfo>.Fail("task not found", RespCodes.NotFound));
        });

        // POST /api/task/cancel?tid= — cancel a running task
        g.MapPost("/cancel", async (HttpContext ctx, ITaskService tasks) =>
        {
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            var type = InferType(tid);
            var r = await tasks.CancelAsync(type, tid, ctx.RequestAborted);
            return Results.Ok(r);
        });

        // POST /api/task/delete?tid= — delete a task
        g.MapPost("/delete", async (HttpContext ctx, ITaskService tasks) =>
        {
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            var type = InferType(tid);
            var r = await tasks.DeleteAsync(type, tid, ctx.RequestAborted);
            return Results.Ok(r);
        });

        // POST /api/task/clear_done?type= — clear all finished tasks of a type
        g.MapPost("/clear_done", async (HttpContext ctx, ITaskService tasks) =>
        {
            var type = ParseType(ctx.Request.Query["type"]);
            var r = await tasks.ClearAsync(type, ctx.RequestAborted);
            return Results.Ok(r);
        });

        // POST /api/task/retry?tid= — retry a failed/canceled task
        g.MapPost("/retry", async (HttpContext ctx, ITaskService tasks) =>
        {
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            var type = InferType(tid);
            var r = await tasks.RetryAsync(type, tid, ctx.RequestAborted);
            return Results.Ok(r);
        });

        return app;
    }

    private static TaskType ParseType(string? value)
    {
        if (string.IsNullOrEmpty(value)) return TaskType.Copy;
        return Enum.TryParse<TaskType>(value, ignoreCase: true, out var t) ? t : TaskType.Copy;
    }

    /// <summary>Task ids are formatted as "{type-lower}-{n}". Infer the type from the prefix.</summary>
    private static TaskType InferType(string tid)
    {
        var idx = tid.IndexOf('-');
        if (idx <= 0) return TaskType.Copy;
        var prefix = tid[..idx];
        return Enum.TryParse<TaskType>(prefix, ignoreCase: true, out var t) ? t : TaskType.Copy;
    }
}
