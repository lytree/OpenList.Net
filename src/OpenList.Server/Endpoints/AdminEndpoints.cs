using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static partial class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin").RequireAuthorization(policy => policy.RequireRole("2"));

        MapUserAdmin(group);
        MapStorageAdmin(group);
        MapMetaAdmin(group);
        MapSettingAdmin(group);
        MapShareAdmin(group);
        MapDriverAdmin(group);
        MapIndexAdmin(group);
        MapMessageAdmin(group);
        MapScanAdmin(group);
        MapTaskAdmin(group);
        MapArchiveAdmin(group);

        return endpoints;
    }

    private static void MapUserAdmin(RouteGroupBuilder group)
    {
        group.MapGet("user/list", async (IUserService users, int page = 1, int perPage = 100) =>
        {
            var (userList, total) = await users.ListUsersAsync(page, perPage);
            return Results.Json(new Resp<PagedListResp<User>>(new PagedListResp<User> { Content = userList, Total = total }));
        });

        group.MapGet("user/get", async (IUserService users, uint id) =>
        {
            var user = await users.GetUserByIdAsync(id);
            if (user == null) return Results.Json(Resp<User>.Error(404, "user not found"));
            return Results.Json(new Resp<User>(user));
        });

        group.MapPost("user/create", async (IUserService users, [FromBody] CreateUserReq req) =>
        {
            var user = new User
            {
                Username = req.Username,
                PwdHash = UserService.StaticHash(req.Password),
                Permission = req.Permission,
                BasePath = req.BasePath ?? "/",
                Role = (UserRole)req.Role,
                Disabled = req.Disabled,
            };
            var created = await users.CreateUserAsync(user);
            return Results.Json(new Resp<IdResp>(new IdResp { Id = created.Id }));
        });

        group.MapPost("user/update", async (IUserService users, [FromBody] UpdateUserReq req) =>
        {
            var user = await users.GetUserByIdAsync(req.Id);
            if (user == null) return Results.Json(Resp<object>.Error(404, "user not found"));

            if (!string.IsNullOrEmpty(req.Password))
                user.PwdHash = UserService.StaticHash(req.Password);
            if (req.Permission.HasValue) user.Permission = req.Permission.Value;
            if (req.BasePath != null) user.BasePath = req.BasePath;
            if (req.Role.HasValue) user.Role = (UserRole)req.Role.Value;
            if (req.Disabled.HasValue) user.Disabled = req.Disabled.Value;

            await users.UpdateUserAsync(user);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("user/delete", async (IUserService users, uint id) =>
        {
            await users.DeleteUserAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("user/cancel_2fa", async (IUserService users, uint id) =>
        {
            await users.Cancel2FAAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("user/del_cache", async (IUserService users, string username) =>
        {
            await users.DeleteUserCacheByUsernameAsync(username);
            return Results.Json(new Resp<object>(null));
        });

        group.MapGet("user/sshkey/list", async (IUserService users, uint uid, int page = 1, int perPage = 100) =>
        {
            var (keys, total) = await users.ListSshKeysPagedAsync(uid, page, perPage);
            return Results.Json(new Resp<PagedListResp<SshKey>>(new PagedListResp<SshKey> { Content = keys, Total = total }));
        });

        group.MapPost("user/sshkey/delete", async (IUserService users, uint id) =>
        {
            await users.DeleteSshKeyByIdAsync(id);
            return Results.Json(new Resp<object>(null));
        });
    }

    private static void MapShareAdmin(RouteGroupBuilder group)
    {
        group.MapGet("share/list", async (ISharingService sharings, int page = 1, int perPage = 100) =>
        {
            var (sharingList, total) = await sharings.ListSharingsAsync(0, page, perPage);
            foreach (var s in sharingList)
            {
                if (s.Creator != null)
                {
                    s.CreatorName = s.Creator.Username;
                    s.CreatorRole = (int)s.Creator.Role;
                }
            }
            return Results.Json(new Resp<PagedListResp<Sharing>>(new PagedListResp<Sharing> { Content = sharingList, Total = total }));
        });

        group.MapPost("share/create", async (ISharingService sharings, Sharing req) =>
        {
            var sharing = await sharings.CreateSharingAsync(req);
            return Results.Json(new Resp<Sharing>(sharing));
        });

        group.MapPost("share/update", async (ISharingService sharings, Sharing req) =>
        {
            await sharings.UpdateSharingAsync(req);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("share/delete", async (ISharingService sharings, string id) =>
        {
            await sharings.DeleteSharingAsync(id);
            return Results.Json(new Resp<object>(null));
        });
    }

    private static void MapDriverAdmin(RouteGroupBuilder group)
    {
        group.MapGet("driver/list", (IStorageService storages) =>
        {
            var infos = storages.ListDriverInfos();
            var map = infos.ToDictionary(
                i => i.Config.Name,
                i => new DriverInfoResp
                {
                    Common = i.Common, Additional = i.Additional, Config = i.Config
                });
            return Results.Json(new Resp<Dictionary<string, DriverInfoResp>>(map));
        });

        group.MapGet("driver/names", (IStorageService storages) =>
            Results.Json(new Resp<List<string>>(storages.ListDriverNames())));

        group.MapGet("driver/info", (IStorageService storages, string driver) =>
        {
            var info = storages.GetDriverInfo(driver);
            return Results.Json(new Resp<DriverInfoResp>(new DriverInfoResp
            {
                Name = info.Config.Name, Common = info.Common, Additional = info.Additional, Config = info.Config
            }));
        });
    }
}
