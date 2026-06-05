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

        // === User management ===
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
                }).ToList(),
                Total = list.Count
            }));
        });

        group.MapGet("user/get", async (IUserService users, uint id) =>
        {
            var u = await users.GetUserByIdAsync(id);
            if (u == null) return Results.Json(Resp<UserResp>.Error(404, "user not found"));
            return Results.Json(new Resp<UserResp>(new UserResp
            {
                Id = u.Id, Username = u.Username, Role = (int)u.Role,
                BasePath = u.BasePath, Permission = u.Permission,
                Disabled = u.Disabled, Otp = !string.IsNullOrEmpty(u.OtpSecret)
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

        group.MapPost("user/del_cache", async (IUserService users, DeleteReq req) =>
        {
            await users.DeleteUserCacheAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        // Admin SSH key management
        group.MapGet("user/sshkey/list", async (IUserService users, uint? id) =>
        {
            if (id.HasValue)
                return Results.Json(new Resp<List<SshKey>>(await users.ListSshKeysAsync(id.Value)));
            return Results.Json(new Resp<List<SshKey>>([]));
        });

        group.MapPost("user/sshkey/delete", async (IUserService users, DeleteSshKeyReq req) =>
        {
            // Admin can delete any user's SSH key - userId passed in request
            await users.DeleteSshKeyAsync(req.UserId, req.Name);
            return Results.Json(new Resp<object>(null));
        });

        // === Storage management ===
        group.MapGet("storage/list", async (IStorageService storages, int page = 1, int perPage = 100) =>
        {
            var list = await storages.ListStoragesAsync(page, perPage);
            return Results.Json(new Resp<PagedListResp<Storage>>(new PagedListResp<Storage> { Content = list, Total = list.Count }));
        });

        group.MapGet("storage/get", async (IStorageService storages, uint id) =>
        {
            var storage = await storages.GetStorageByIdAsync(id);
            if (storage == null) return Results.Json(Resp<Storage>.Error(404, "storage not found"));
            return Results.Json(new Resp<Storage>(storage));
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

        group.MapPost("storage/load_all", async (IStorageService storages) =>
        {
            await storages.LoadAllStoragesAsync();
            return Results.Json(new Resp<object>(null));
        });

        // === Meta management ===
        group.MapGet("meta/list", async (IMetaService metas) =>
            Results.Json(new Resp<List<Meta>>(await metas.ListMetasAsync())));

        group.MapGet("meta/get", async (IMetaService metas, string path) =>
        {
            var meta = await metas.GetMetaAsync(path);
            if (meta == null) return Results.Json(Resp<Meta>.Error(404, "meta not found"));
            return Results.Json(new Resp<Meta>(meta));
        });

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

        // === Settings management ===
        group.MapGet("setting/list", async (ISettingService settings) =>
            Results.Json(new Resp<List<SettingItem>>(await settings.ListSettingsAsync())));

        group.MapGet("setting/get", async (ISettingService settings, string key) =>
        {
            var value = await settings.GetSettingAsync(key);
            if (value == null) return Results.Json(Resp<string>.Error(404, "setting not found"));
            return Results.Json(new Resp<string>(value));
        });

        group.MapPost("setting/save", async (ISettingService settings, List<SettingItem> items) =>
        {
            await settings.SaveSettingsAsync(items);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("setting/delete", async (ISettingService settings, DeleteSettingReq req) =>
        {
            await settings.DeleteSettingAsync(req.Key);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("setting/default", async (ISettingService settings) =>
        {
            await settings.DefaultSettingsAsync();
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("setting/reset_token", async (ISettingService settings) =>
        {
            await settings.ResetTokenAsync();
            return Results.Json(new Resp<object>(null));
        });

        // Offline download tool settings
        group.MapPost("setting/set_aria2", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_qbit", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_transmission", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_115", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_115_open", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_123_pan", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_123_open", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_pikpak", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunder", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunderx", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunder_browser", (SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        // === Sharing management (admin view) ===
        group.MapGet("share/list", async (ISharingService sharings, int page = 1, int perPage = 100) =>
        {
            // Admin sees all sharings
            var list = await sharings.ListSharingsAsync(0);
            return Results.Json(new Resp<PagedListResp<Sharing>>(new PagedListResp<Sharing> { Content = list, Total = list.Count }));
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

        // === Driver info ===
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

        // === Search index management ===
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

        // === Message management ===
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

        // === Scan management ===
        group.MapPost("scan/start", () => Results.Json(new Resp<object>(null)));
        group.MapPost("scan/stop", () => Results.Json(new Resp<object>(null)));
        group.MapGet("scan/progress", () => Results.Json(new Resp<object>(new { progress = 0, state = "idle" })));

        // === Admin task routes (legacy compatibility) ===
        group.MapGet("task/list", async (ITaskService tasks, string? type = null) =>
            Results.Json(new Resp<List<TaskInfo>>(await tasks.ListTasksAsync(type))));

        group.MapGet("task/info", async (ITaskService tasks, string id) =>
        {
            var task = await tasks.GetTaskAsync(id);
            if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
            return Results.Json(new Resp<TaskInfo>(task));
        });

        group.MapPost("task/cancel", async (ITaskService tasks, TaskIdReq req) =>
        {
            await tasks.CancelTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("task/retry", async (ITaskService tasks, TaskIdReq req) =>
        {
            await tasks.RetryTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("task/delete", async (ITaskService tasks, TaskIdReq req) =>
        {
            await tasks.DeleteTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }
}
