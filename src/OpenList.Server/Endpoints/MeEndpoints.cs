using System.Security.Claims;
using Mapster;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/me").RequireAuthorization();

        group.MapGet("", async (IUserService users, ClaimsPrincipal user) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<UserResp>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<UserResp>.Error(404, "user not found"));
            return Results.Json(new Resp<UserResp>(u.Adapt<UserResp>()));
        }).WithSummary("获取当前用户信息");

        group.MapPost("update", async (IUserService users, IAuthService auth, ClaimsPrincipal user, [FromBody] UpdateMeReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(404, "user not found"));
            if (u.IsGuest) return Results.Json(Resp<object>.Error(403, "guest cannot update profile"));

            // Go: always set username and sso_id (even if empty)
            u.Username = req.Username ?? u.Username;
            if (!string.IsNullOrEmpty(req.Password)) await users.SetPasswordAsync(u, req.Password);
            u.SsoID = req.SsoID ?? u.SsoID;

            await users.UpdateUserAsync(u);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("更新个人资料");

        group.MapGet("sshkey/list", async (IUserService users, ClaimsPrincipal user, int page = 1, int perPage = 100) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<PagedListResp<SshKey>>.Error(401, "unauthorized"));
            var (keys, total) = await users.ListSshKeysPagedAsync(userId.Value, page, perPage);
            return Results.Json(new Resp<PagedListResp<SshKey>>(new PagedListResp<SshKey> { Content = keys, Total = total }));
        }).WithSummary("获取 SSH 密钥列表");

        group.MapPost("sshkey/add", async (IUserService users, ClaimsPrincipal user, [FromBody] AddSshKeyReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            await users.AddSshKeyAsync(userId.Value, req.Name, req.Key);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("添加 SSH 密钥");

        group.MapPost("sshkey/delete", async (IUserService users, ClaimsPrincipal user, uint id) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            await users.DeleteSshKeyByIdAsync(id);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("删除 SSH 密钥");

        return endpoints;
    }

}
