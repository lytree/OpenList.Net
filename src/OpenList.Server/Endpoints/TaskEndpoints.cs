using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 任务管理端点。路由采用前端 <c>/task/{type}/...</c> 形态（类型放 URL 路径而非查询串）。
/// 对应 OpenList Go <c>server/handles/task.go</c>。所有端点需登录鉴权。
/// 支持 list / info / cancel / delete / retry / clear_done / clear_succeeded /
/// retry_failed / cancel_some / delete_some / retry_some 等操作。
/// </summary>
public static class TaskEndpoints
{
    /// <summary>
    /// 注册 <c>/api/task</c> 任务路由（需登录）。具体动作统一由
    /// <see cref="MapTaskRoutes"/> 在一个子组上注册。
    /// </summary>
    public static IEndpointRouteBuilder MapTaskApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/task").WithTags("Task").RequireAuthorization();
        g.MapTaskRoutes();
        return app;
    }

    /// <summary>
    /// 在任意组上注册任务路由面。<see cref="MapTaskApi"/>（/api/task）与管理员别名
    /// /api/admin/task（Go 为兼容旧自动化而保留）都会使用它。
    /// 提供 list/info/{cancel,delete,retry,clear_done,clear_succeeded,retry_failed,
    /// cancel_some,delete_some,retry_some} 等端点，均需登录。
    /// </summary>
    public static void MapTaskRoutes(this IEndpointRouteBuilder g)
    {
        // 归属判定：普通用户只能操作自己创建的任务，管理员可以管理全部。
        // 任务 id 是递增可猜的（"copy-1"、"copy-2"…），不做归属校验时任何登录用户
        // 都能取消/删除别人的复制、移动与离线下载任务。
        static bool CanManage(ClaimsPrincipal principal, ITaskService tasks, string tid)
        {
            var u = principal.ToUser();
            return u.IsAdmin() || tasks.OwnerOf(tid) == u.Id;
        }

        static List<string> OwnedIds(ClaimsPrincipal principal, ITaskService tasks, IEnumerable<string> tids)
        {
            var u = principal.ToUser();
            return u.IsAdmin() ? tids.ToList() : tids.Where(t => tasks.OwnerOf(t) == u.Id).ToList();
        }

        // GET /api/task/{type}/{done} — list tasks. {done} is "done" or "undone".
        g.MapGet("/{type}/{done}", async (string type, string done, ITaskService tasks, ClaimsPrincipal user, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            var r = done switch
            {
                "done" => await tasks.DoneAsync(taskType, ctx.RequestAborted),
                "undone" => await tasks.UndoneAsync(taskType, ctx.RequestAborted),
                _ => Resp<List<TaskInfo>>.Fail($"invalid list state: {done}", RespCodes.BadRequest),
            };
            var u = user.ToUser();
            if (r.Code == RespCodes.Ok && r.Data is not null && !u.IsAdmin())
                r = Resp<List<TaskInfo>>.Ok(r.Data.Where(t => t.OwnerId == u.Id).ToList());
            return Results.Ok(r);
        });

        // GET /api/task/info?tid= — get task info (no type prefix; searches all types).
        g.MapGet("/info", async (HttpContext ctx, ITaskService tasks, ClaimsPrincipal user) =>
        {
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            if (!CanManage(user, tasks, tid))
                return Results.Ok(Resp<TaskInfo>.Fail("task not found", RespCodes.NotFound));
            foreach (TaskType type in Enum.GetValues<TaskType>())
            {
                var r = await tasks.InfoAsync(type, tid, ctx.RequestAborted);
                if (r.Code == 200) return Results.Ok(r);
            }
            return Results.Ok(Resp<TaskInfo>.Fail("task not found", RespCodes.NotFound));
        });

        // POST /api/task/{type}/cancel?tid= — cancel a running task
        g.MapPost("/{type}/cancel", async (string type, HttpContext ctx, ITaskService tasks, ClaimsPrincipal user) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            if (!CanManage(user, tasks, tid))
                return Results.Ok(Resp.Fail("task not found", RespCodes.NotFound));
            return Results.Ok(await tasks.CancelAsync(taskType, tid, ctx.RequestAborted));
        });

        // POST /api/task/{type}/delete?tid= — delete a task
        g.MapPost("/{type}/delete", async (string type, HttpContext ctx, ITaskService tasks, ClaimsPrincipal user) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            if (!CanManage(user, tasks, tid))
                return Results.Ok(Resp.Fail("task not found", RespCodes.NotFound));
            return Results.Ok(await tasks.DeleteAsync(taskType, tid, ctx.RequestAborted));
        });

        // POST /api/task/{type}/retry?tid= — retry a failed/canceled task
        g.MapPost("/{type}/retry", async (string type, HttpContext ctx, ITaskService tasks, ClaimsPrincipal user) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            var tid = ctx.Request.Query["tid"].ToString();
            if (string.IsNullOrEmpty(tid))
                return Results.Ok(Resp.Fail("tid is required", RespCodes.BadRequest));
            if (!CanManage(user, tasks, tid))
                return Results.Ok(Resp.Fail("task not found", RespCodes.NotFound));
            return Results.Ok(await tasks.RetryAsync(taskType, tid, ctx.RequestAborted));
        });

        // POST /api/task/{type}/clear_done — clear all finished tasks of a type.
        // 普通用户只清自己的：批量清理必须按归属过滤，否则一次点击就清掉别人的任务记录。
        g.MapPost("/{type}/clear_done", async (string type, ITaskService tasks, ClaimsPrincipal user, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            if (user.ToUser().IsAdmin())
                return Results.Ok(await tasks.ClearAsync(taskType, ctx.RequestAborted));

            var done = await tasks.DoneAsync(taskType, ctx.RequestAborted);
            var ids = OwnedIds(user, tasks, (done.Data ?? []).Select(t => t.Id));
            if (ids.Count == 0) return Results.Ok(Resp.Ok());
            return Results.Ok(await tasks.DeleteSomeAsync(taskType, ids, ctx.RequestAborted));
        });

        // POST /api/task/{type}/clear_succeeded — clear only succeeded tasks
        g.MapPost("/{type}/clear_succeeded", async (string type, ITaskService tasks, ClaimsPrincipal user, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            if (user.ToUser().IsAdmin())
                return Results.Ok(await tasks.ClearSucceededAsync(taskType, ctx.RequestAborted));

            var done = await tasks.DoneAsync(taskType, ctx.RequestAborted);
            var ids = OwnedIds(user, tasks,
                (done.Data ?? []).Where(t => t.State == TaskState.Succeeded).Select(t => t.Id));
            if (ids.Count == 0) return Results.Ok(Resp.Ok());
            return Results.Ok(await tasks.DeleteSomeAsync(taskType, ids, ctx.RequestAborted));
        });

        // POST /api/task/{type}/retry_failed — retry all failed/canceled tasks of a type
        g.MapPost("/{type}/retry_failed", async (string type, ITaskService tasks, ClaimsPrincipal user, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            if (user.ToUser().IsAdmin())
                return Results.Ok(await tasks.RetryFailedAsync(taskType, ctx.RequestAborted));

            var done = await tasks.DoneAsync(taskType, ctx.RequestAborted);
            var ids = OwnedIds(user, tasks,
                (done.Data ?? []).Where(t => t.State is TaskState.Errored or TaskState.Canceled).Select(t => t.Id));
            if (ids.Count == 0) return Results.Ok(Resp.Ok());
            return Results.Ok(await tasks.RetrySomeAsync(taskType, ids, ctx.RequestAborted));
        });

        // POST /api/task/{type}/cancel_some — batch cancel; body is string[] of tids.
        g.MapPost("/{type}/cancel_some", async (string type, List<string> tids, ITaskService tasks, ClaimsPrincipal user, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await GuardBatchAsync(user, tasks, taskType, tids, tasks.CancelSomeAsync, ctx.RequestAborted));
        });

        // POST /api/task/{type}/delete_some — batch delete
        g.MapPost("/{type}/delete_some", async (string type, List<string> tids, ITaskService tasks, ClaimsPrincipal user, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await GuardBatchAsync(user, tasks, taskType, tids, tasks.DeleteSomeAsync, ctx.RequestAborted));
        });

        // POST /api/task/{type}/retry_some — batch retry
        g.MapPost("/{type}/retry_some", async (string type, List<string> tids, ITaskService tasks, ClaimsPrincipal user, HttpContext ctx) =>
        {
            if (!TryParseType(type, out var taskType))
                return Results.Ok(Resp.Fail($"unknown task type: {type}", RespCodes.BadRequest));
            return Results.Ok(await GuardBatchAsync(user, tasks, taskType, tids, tasks.RetrySomeAsync, ctx.RequestAborted));
        });
    }

    /// <summary>
    /// 批量操作前按归属过滤，并把不属于调用方的 tid 作为错误项返回
    /// （与 Go「按 tid 组织错误信息」的返回结构一致，调用方无需改解析逻辑）。
    /// </summary>
    private static async Task<Resp<Dictionary<string, string>>> GuardBatchAsync(
        ClaimsPrincipal principal,
        ITaskService tasks,
        TaskType taskType,
        IReadOnlyList<string> tids,
        Func<TaskType, IReadOnlyList<string>, CancellationToken, Task<Resp<Dictionary<string, string>>>> action,
        CancellationToken ct)
    {
        var u = principal.ToUser();
        var allowed = new List<string>();
        var errors = new Dictionary<string, string>();
        foreach (var tid in tids)
        {
            if (u.IsAdmin() || tasks.OwnerOf(tid) == u.Id) allowed.Add(tid);
            else errors[tid] = "task not found";
        }

        if (allowed.Count == 0) return Resp<Dictionary<string, string>>.Ok(errors);
        var r = await action(taskType, allowed, ct);
        if (r.Code != RespCodes.Ok) return r;
        if (r.Data is not null)
            foreach (var kv in r.Data) errors[kv.Key] = kv.Value;
        return Resp<Dictionary<string, string>>.Ok(errors);
    }

    // 前端以 snake_case 字符串发送任务类型（如 "offline_download"、"decompress_upload"、
    // "aria2_down"）。C# 枚举名是 PascalCase，Enum.TryParse 无法匹配下划线形式，
    // 因此显式建立别名映射。
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
