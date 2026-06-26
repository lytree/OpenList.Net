using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Task management endpoints. Routes use the frontend's <c>/task/{type}/...</c>
/// pattern (type in the URL path, not query string). Mirrors OpenList Go
/// <c>server/handles/task.go</c>. All endpoints require authentication.
/// </summary>
public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/task").WithTags("Task").RequireAuthorization();

        // GET /api/task/{type}/{done} — list tasks. {done} is "done" or "undone".
        g.MapGet("/{type}/{done}", async (string type, string done, ITaskService tasks, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            var r = done switch
            {
                "done" => await tasks.DoneAsync(taskType, ctx.RequestAborted),
                "undone" => await tasks.UndoneAsync(taskType, ctx.RequestAborted),
                _ => Resp<List<TaskInfo>>.Fail($"invalid list state: {done}", RespCodes.BadRequest),
            };
            return Results.Ok(r);
        });

        // GET /api/task/info?tid= — get task info (no type prefix; searches all types).
        g.MapGet("/info", async (HttpContext ctx, ITaskService tasks) =>
        {
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            foreach (TaskType type in Enum.GetValues<TaskType>())
            {
                var r = await tasks.InfoAsync(type, tid, ctx.RequestAborted);
                if (r.Code == 200) return Results.Ok(r);
            }
            return Results.Ok(Resp<TaskInfo>.Fail("task not found", RespCodes.NotFound));
        });

        // POST /api/task/{type}/cancel?tid= — cancel a running task
        g.MapPost("/{type}/cancel", async (string type, HttpContext ctx, ITaskService tasks) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            return Results.Ok(await tasks.CancelAsync(taskType, tid, ctx.RequestAborted));
        });

        // POST /api/task/{type}/delete?tid= — delete a task
        g.MapPost("/{type}/delete", async (string type, HttpContext ctx, ITaskService tasks) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            return Results.Ok(await tasks.DeleteAsync(taskType, tid, ctx.RequestAborted));
        });

        // POST /api/task/{type}/retry?tid= — retry a failed/canceled task
        g.MapPost("/{type}/retry", async (string type, HttpContext ctx, ITaskService tasks) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            return Results.Ok(await tasks.RetryAsync(taskType, tid, ctx.RequestAborted));
        });

        // POST /api/task/{type}/clear_done — clear all finished tasks of a type
        g.MapPost("/{type}/clear_done", async (string type, ITaskService tasks, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await tasks.ClearAsync(taskType, ctx.RequestAborted));
        });

        // POST /api/task/{type}/clear_succeeded — clear only succeeded tasks
        g.MapPost("/{type}/clear_succeeded", async (string type, ITaskService tasks, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await tasks.ClearSucceededAsync(taskType, ctx.RequestAborted));
        });

        // POST /api/task/{type}/retry_failed — retry all failed/canceled tasks of a type
        g.MapPost("/{type}/retry_failed", async (string type, ITaskService tasks, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await tasks.RetryFailedAsync(taskType, ctx.RequestAborted));
        });

        // POST /api/task/{type}/cancel_some — batch cancel; body is string[] of tids.
        // Returns { data: { tid: errorMsg, ... } } — empty object means all succeeded.
        g.MapPost("/{type}/cancel_some", async (string type, List<string> tids, ITaskService tasks, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await tasks.CancelSomeAsync(taskType, tids, ctx.RequestAborted));
        });

        // POST /api/task/{type}/delete_some — batch delete
        g.MapPost("/{type}/delete_some", async (string type, List<string> tids, ITaskService tasks, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await tasks.DeleteSomeAsync(taskType, tids, ctx.RequestAborted));
        });

        // POST /api/task/{type}/retry_some — batch retry
        g.MapPost("/{type}/retry_some", async (string type, List<string> tids, ITaskService tasks, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await tasks.RetrySomeAsync(taskType, tids, ctx.RequestAborted));
        });

        return app;
    }

    // Frontend sends task types as snake_case strings (e.g. "offline_download",
    // "decompress_upload", "aria2_down"). The C# enum uses PascalCase names, so
    // Enum.TryParse won't match the snake_case forms — map them explicitly.
    private static readonly Dictionary<string, TaskType> TypeAliases = new(StringComparer.Ordinal)
    {
        ["copy"] = TaskType.Copy,
        ["upload"] = TaskType.Upload,
        ["transfer"] = TaskType.Transfer,
        ["download"] = TaskType.Download,
        ["decompress"] = TaskType.Decompress,
        ["decompress_upload"] = TaskType.DecompressUpload,
        ["move"] = TaskType.Move,
        ["offline_download"] = TaskType.OfflineDownload,
        ["offline_download_transfer"] = TaskType.OfflineDownloadTransfer,
        ["aria2_down"] = TaskType.Aria2Down,
        ["aria2_transfer"] = TaskType.Aria2Transfer,
        ["qbit_down"] = TaskType.QbitDown,
        ["qbit_transfer"] = TaskType.QbitTransfer,
        ["transmission_down"] = TaskType.TransmissionDown,
        ["transmission_transfer"] = TaskType.TransmissionTransfer,
    };

    private static bool TryParseType(string? value, out TaskType type)
    {
        if (value is not null && TypeAliases.TryGetValue(value, out type))
            return true;
        return Enum.TryParse(value, ignoreCase: true, out type);
    }
}
