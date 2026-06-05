using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin").RequireAuthorization(policy => policy.RequireRole("2"));

        // User management
        group.MapGet("user/list", async (IUserService users, int page = 1, int perPage = 100) =>
        {
            var list = await users.ListUsersAsync(page, perPage);
            return Results.Json(new Resp<PagedListResp<UserResp>>(new PagedListResp<UserResp>
            {
                Content = list.Select(u => new UserResp
                {
                    Id = u.Id, Username = u.Username, Role = (int)u.Role,
                    BasePath = u.BasePath, Permission = u.Permission,
                    Disabled = u.Disabled, Otp = !string.IsNullOrEmpty(u.OtpSecret)
                }).ToList()
            }));
        });

        group.MapPost("user/create", async (IUserService users, CreateUserReq req) =>
        {
            var user = new User { Username = req.Username, BasePath = req.BasePath ?? "/", Role = (UserRole)req.Role, Permission = req.Permission, Disabled = req.Disabled };
            await users.CreateUserAsync(user);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("user/update", async (IUserService users, UpdateUserReq req) =>
        {
            var user = await users.GetUserByIdAsync(req.Id);
            if (user == null) return Results.Json(Resp<object>.Error(404, "user not found"));
            if (req.Username != null) user.Username = req.Username;
            if (req.BasePath != null) user.BasePath = req.BasePath;
            if (req.Role != null) user.Role = (UserRole)req.Role.Value;
            if (req.Permission != null) user.Permission = req.Permission.Value;
            if (req.Disabled != null) user.Disabled = req.Disabled.Value;
            await users.UpdateUserAsync(user);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("user/delete", async (IUserService users, DeleteReq req) =>
        {
            await users.DeleteUserAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("user/cancel_2fa", async (IUserService users, DeleteReq req) =>
        {
            await users.Cancel2FAAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        // Storage management
        group.MapGet("storage/list", async (IStorageService storages, int page = 1, int perPage = 100) =>
        {
            var list = await storages.ListStoragesAsync(page, perPage);
            return Results.Json(new Resp<PagedListResp<Storage>>(new PagedListResp<Storage> { Content = list }));
        });

        group.MapPost("storage/create", async (IStorageService storages, Storage req) =>
        {
            var id = await storages.CreateStorageAsync(req);
            return Results.Json(new Resp<IdResp>(new IdResp { Id = id }));
        });

        group.MapPost("storage/update", async (IStorageService storages, Storage req) =>
        {
            await storages.UpdateStorageAsync(req);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("storage/delete", async (IStorageService storages, DeleteReq req) =>
        {
            await storages.DeleteStorageAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("storage/enable", async (IStorageService storages, DeleteReq req) =>
        {
            await storages.EnableStorageAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("storage/disable", async (IStorageService storages, DeleteReq req) =>
        {
            await storages.DisableStorageAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapGet("storage/drivers", (IStorageService storages) =>
        {
            var infos = storages.ListDriverInfos();
            return Results.Json(new Resp<List<DriverInfoResp>>(infos.Select(i => new DriverInfoResp
            {
                Name = i.Config.Name, Common = i.Common, Additional = i.Additional, Config = i.Config
            }).ToList()));
        });

        // Meta management
        group.MapGet("meta/list", async (IMetaService metas) =>
            Results.Json(new Resp<List<Meta>>(await metas.ListMetasAsync())));

        group.MapPost("meta/create", async (IMetaService metas, Meta req) =>
        {
            await metas.CreateMetaAsync(req);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("meta/update", async (IMetaService metas, Meta req) =>
        {
            await metas.UpdateMetaAsync(req);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("meta/delete", async (IMetaService metas, DeleteMetaReq req) =>
        {
            await metas.DeleteMetaAsync(req.Path);
            return Results.Json(new Resp<object>(null));
        });

        // Settings management
        group.MapGet("setting/list", async (ISettingService settings) =>
            Results.Json(new Resp<List<SettingItem>>(await settings.ListSettingsAsync())));

        group.MapPost("setting/save", async (ISettingService settings, List<SettingItem> items) =>
        {
            await settings.SaveSettingsAsync(items);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("setting/reset_token", async (ISettingService settings) =>
        {
            await settings.ResetTokenAsync();
            return Results.Json(new Resp<object>(null));
        });

        // Sharing management
        group.MapGet("share/list", async (ISharingService sharings, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            var list = await sharings.ListSharingsAsync(userId ?? 0);
            return Results.Json(new Resp<List<Sharing>>(list));
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

        group.MapPost("share/delete", async (ISharingService sharings, DeleteShareReq req) =>
        {
            await sharings.DeleteSharingAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        // Driver info
        group.MapGet("driver/list", (IStorageService storages) =>
        {
            var infos = storages.ListDriverInfos();
            return Results.Json(new Resp<List<DriverInfoResp>>(infos.Select(i => new DriverInfoResp
            {
                Name = i.Config.Name, Common = i.Common, Additional = i.Additional, Config = i.Config
            }).ToList()));
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

        // Search index management
        group.MapPost("index/build", (ISearchService search) =>
        {
            _ = Task.Run(() => search.BuildIndexAsync());
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/update", (ISearchService search) =>
        {
            _ = Task.Run(() => search.UpdateIndexAsync("/", []));
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/stop", async (ISearchService search) =>
        {
            await search.StopIndexAsync();
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/clear", async (ISearchService search) =>
        {
            await search.ClearIndexAsync();
            return Results.Json(new Resp<object>(null));
        });

        group.MapGet("index/progress", async (ISearchService search) =>
            Results.Json(new Resp<IndexProgress>(await search.GetProgressAsync())));

        // Message management
        group.MapPost("message/get", async (IMessageService messages, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<List<Message>>.Error(401, "unauthorized"));
            return Results.Json(new Resp<List<Message>>(await messages.GetMessagesAsync(userId.Value)));
        });

        group.MapPost("message/send", async (IMessageService messages, SendMessageReq req) =>
        {
            await messages.SendMessageAsync(req.UserId, req.Title, req.Content);
            return Results.Json(new Resp<object>(null));
        });

        // Scan management
        group.MapPost("scan/start", () => Results.Json(new Resp<object>(null)));
        group.MapPost("scan/stop", () => Results.Json(new Resp<object>(null)));
        group.MapGet("scan/progress", () => Results.Json(new Resp<object>(new { progress = 0, state = "idle" })));

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }
}
