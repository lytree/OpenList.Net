using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 分享管理端点，路由位于 <c>/api/share</c>。
/// 对应 OpenList Go <c>server/handles/sharing.go</c>。
/// 全部端点要求登录（<c>RequireAuthorization()</c>）。
/// 前端通过用户可见的分享码（code，字符串）经 <c>?id=</c> 进行管理查询，
/// 而非数据库主键。
/// 提供 list、get、create、update、delete、enable、disable 七类管理操作。
/// </summary>
public static class ShareEndpoints
{
    /// <summary>
    /// 注册 /api/share 路由组（需鉴权）。
    /// 所有操作按分享码（?id=）定位记录，并依据当前用户身份
    /// （<see cref="ClaimsPrincipal.UserId"/> 与 <see cref="ClaimsPrincipal.IsAdmin"/>）
    /// 决定可见范围（普通用户仅自己，管理员全部）。
    /// </summary>
    public static IEndpointRouteBuilder MapShareApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/share").WithTags("Share").RequireAuthorization();

        // GET /api/share/list — 列出当前用户（管理员为全部）的分享。
        // Go 通过 g.Any(...) 注册 —— 这里同时接受 GET 与 POST。
        // 包装为 { content, total } 以适配前端 Shares.tsx。
        g.MapMethods("/list", ["GET", "POST"], async (ISharingService svc, ClaimsPrincipal user) =>
        {
            var r = await svc.ListAsync(user.UserId() ?? 0, user.IsAdmin());
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data, total = r.Data.Count }));
        });

        // GET /api/share/get?id={code} — 按分享码获取单条分享详情。
        g.MapGet("/get", async (HttpContext ctx, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            // id 缺失时返回 400 以快速提示前端参数错误。
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.GetAsync(code, user.UserId() ?? 0, user.IsAdmin()));
        });

        // POST /api/share/create — 创建新分享（body 为 ShareReq）。
        g.MapPost("/create", async (ShareReq req, ISharingService svc, ClaimsPrincipal user) =>
            Results.Ok(await svc.CreateAsync(req, user.UserId() ?? 0, user.IsAdmin())));

        // POST /api/share/update?id={code} — 更新已存在的分享。
        g.MapPost("/update", async (HttpContext ctx, ShareReq req, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.UpdateAsync(code, req, user.UserId() ?? 0, user.IsAdmin()));
        });

        // POST /api/share/delete?id={code} — 删除分享。
        g.MapPost("/delete", async (HttpContext ctx, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.DeleteAsync(code, user.UserId() ?? 0, user.IsAdmin()));
        });

        // POST /api/share/enable?id={code} — 启用分享（重新对外开放）。
        g.MapPost("/enable", async (HttpContext ctx, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.EnableAsync(code, user.UserId() ?? 0, user.IsAdmin()));
        });

        // POST /api/share/disable?id={code} — 禁用分享（关闭访问入口）。
        g.MapPost("/disable", async (HttpContext ctx, ISharingService svc, ClaimsPrincipal user) =>
        {
            var code = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Ok(Resp.Fail("id is required", RespCodes.BadRequest));
            return Results.Ok(await svc.DisableAsync(code, user.UserId() ?? 0, user.IsAdmin()));
        });

        return app;
    }
}
