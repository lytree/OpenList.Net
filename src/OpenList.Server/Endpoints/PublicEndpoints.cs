using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 公开端点：站点设置、归档扩展名、离线下载工具列表、系统初始化向导（无需鉴权）。
/// 对应 OpenList Go <c>server/handles/public.go</c> 与 <c>server/handles/setup.go</c>。
/// </summary>
public static class PublicEndpoints
{
    /// <summary>
    /// 注册 <c>/api/public</c> 公开路由。各端点均接受 GET/POST 且匿名可访问。
    /// </summary>
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/public").WithTags("Public");

        // Go registers these via public.Any(...) — accept GET or POST.
        // GET|POST /api/public/settings — 返回公开设置项（site_title 等）
        g.MapMethods("/settings", ["GET", "POST"], async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetPublicAsync(ctx.RequestAborted)));

        // GET /api/public/archive_extensions — supported archive extensions
        g.MapMethods("/archive_extensions", ["GET", "POST"], async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetArchiveExtensionsAsync(ctx.RequestAborted)));

        // GET /api/public/offline_download_tools — enabled offline download tool names
        g.MapMethods("/offline_download_tools", ["GET", "POST"], async (ISettingService settings, HttpContext ctx) =>
            Results.Ok(await settings.GetOfflineDownloadToolsAsync(ctx.RequestAborted)));

        // GET|POST /api/public/init_status — 系统是否已完成初始化（是否已存在管理员账号）。
        // 前端安装向导据此判断是否需要跳转到初始化页面。对应 Go handles.InitStatus。
        g.MapMethods("/init_status", ["GET", "POST"], async (IUserService users, HttpContext ctx) =>
        {
            var admin = await users.GetAdminAsync(ctx.RequestAborted);
            var initialized = admin.Code == RespCodes.Ok && admin.Data is not null;
            return Results.Ok(Resp<Dictionary<string, bool>>.Ok(
                new Dictionary<string, bool> { ["initialized"] = initialized }));
        });

        // POST /api/public/init/setup — 执行系统初始化：创建管理员账号并（可选）设置站点名称。
        // 仅在系统尚未初始化时允许调用；已存在管理员则返回 400。对应 Go handles.InitSetup。
        g.MapPost("/init/setup", async (InitSetupReq req, IUserService users, ISettingService settings, HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Ok(Resp.Fail("username and password required", RespCodes.BadRequest));

            // 已初始化（已存在管理员）则拒绝重复初始化
            var existing = await users.GetAdminAsync(ct);
            if (existing.Code == RespCodes.Ok && existing.Data is not null)
                return Results.Ok(Resp.Fail("system has already been initialized", RespCodes.BadRequest));

            // 密码最小长度校验（对齐 Go：>= 4）
            if (req.Password.Length < 4)
                return Results.Ok(Resp.Fail("password must be at least 4 characters", RespCodes.BadRequest));

            // 用 CreateInitialAdminAsync 而非 CreateAsync：向导建的是系统唯一的内置管理员，
            // 必须带 IsBuiltIn 标记，否则装完反而失去"不可删除"的保护。
            var create = await users.CreateInitialAdminAsync(req.Username, req.Password, ct);
            if (create.Code != RespCodes.Ok)
                return Results.Ok(Resp.Fail(create.Message, create.Code));

            // 设置站点名称（如果提供）
            if (!string.IsNullOrEmpty(req.SiteTitle))
            {
                var item = new SettingItem
                {
                    Key = "site_title",
                    Value = req.SiteTitle,
                    Type = SettingValueType.String,
                    Group = (int)SettingGroup.Site,
                    Flag = SettingFlag.Public,
                };
                var save = await settings.SaveAsync([item], ct);
                if (save.Code != RespCodes.Ok)
                    return Results.Ok(Resp.Fail(save.Message, save.Code));
            }

            return Results.Ok(Resp.Ok());
        });

        return app;
    }
}

/// <summary>
/// 系统初始化请求体。对应 Go <c>handles.InitSetupReq</c>。
/// </summary>
public sealed class InitSetupReq
{
    /// <summary>管理员用户名（必填）。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>管理员密码（必填，至少 4 个字符）。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>站点名称（可选，写入 site_title 设置项）。</summary>
    [JsonPropertyName("site_title")]
    public string? SiteTitle { get; set; }
}
