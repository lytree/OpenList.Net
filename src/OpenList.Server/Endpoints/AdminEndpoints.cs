using System.Security.Claims;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
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
            var (userList, total) = await users.ListUsersAsync(page, perPage);
            return Results.Json(new Resp<PagedListResp<UserResp>>(new PagedListResp<UserResp>
            {
                Content = userList.Select(u => new UserResp
                {
                    Id = u.Id, Username = u.Username, Role = (int)u.Role,
                    BasePath = u.BasePath, Permission = u.Permission,
                    Disabled = u.Disabled, Otp = !string.IsNullOrEmpty(u.OtpSecret),
                    SsoID = u.SsoID, AllowLdap = u.AllowLdap
                }).ToList(),
                Total = total
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
                Disabled = u.Disabled, Otp = !string.IsNullOrEmpty(u.OtpSecret),
                SsoID = u.SsoID, AllowLdap = u.AllowLdap
            }));
        });

        group.MapPost("user/create", async (IUserService users, [FromBody] CreateUserReq req) =>
        {
            // Go: admin or guest user can not be created
            if (req.Role == 1 || req.Role == 2)
                return Results.Json(Resp<object>.Error(400, "admin or guest user can not be created"));
            var user = new User { Username = req.Username, BasePath = req.BasePath ?? "/", Role = (UserRole)req.Role, Permission = req.Permission, Disabled = req.Disabled, AllowLdap = req.AllowLdap, Authn = "[]" };
            // Go: always call SetPassword (even for empty password)
            await users.SetPasswordAsync(user, req.Password ?? "");
            await users.CreateUserAsync(user);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("user/update", async (IUserService users, [FromBody] UpdateUserReq req) =>
        {
            var user = await users.GetUserByIdAsync(req.Id);
            if (user == null) return Results.Json(Resp<object>.Error(404, "user not found"));
            // Go: role can not be changed
            if (req.Role != null && req.Role != (int)user.Role)
                return Results.Json(Resp<object>.Error(400, "role can not be changed"));
            // Go: admin user can not be disabled
            if (req.Disabled == true && user.Role == UserRole.Admin)
                return Results.Json(Resp<object>.Error(400, "admin user can not be disabled"));
            if (req.Username != null) user.Username = req.Username;
            if (req.BasePath != null) user.BasePath = req.BasePath;
            if (req.Permission != null) user.Permission = req.Permission.Value;
            if (req.Disabled != null) user.Disabled = req.Disabled.Value;
            if (req.AllowLdap != null) user.AllowLdap = req.AllowLdap.Value;
            // Go: if password is empty, keep old password hash
            if (!string.IsNullOrEmpty(req.Password))
            {
                await users.SetPasswordAsync(user, req.Password);
            }
            await users.UpdateUserAsync(user);
            return Results.Json(new Resp<object>(null));
        });

        // Go: uses query param "id"
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

        // Go: uses query param "username"
        group.MapPost("user/del_cache", async (IUserService users, string username) =>
        {
            await users.DeleteUserCacheByUsernameAsync(username);
            return Results.Json(new Resp<object>(null));
        });

        // Admin SSH key management (Go: uid is required query param, supports pagination)
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

        // === Storage management ===
        group.MapGet("storage/list", async (IStorageService storages, IDriverFactory driverFactory, ISettingService settings, int page = 1, int perPage = 100) =>
        {
            var (storageList, total) = await storages.ListStoragesAsync(page, perPage);
            // Fill MountDetails for each storage (Go: makeStorageResp)
            var hideDetails = settings.GetBool("hide_storage_details_in_manage_page");
            if (!hideDetails)
            {
                foreach (var s in storageList)
                {
                    var driver = driverFactory.GetDriver(s.MountPath);
                    if (driver is IDriverWithDetails withDetails)
                    {
                        try { s.MountDetails = await withDetails.GetDetailsAsync(); }
                        catch { /* ignore errors for storage details */ }
                    }
                }
            }
            return Results.Json(new Resp<PagedListResp<Storage>>(new PagedListResp<Storage> { Content = storageList, Total = total }));
        });

        group.MapGet("storage/get", async (IStorageService storages, IDriverFactory driverFactory, uint id) =>
        {
            var storage = await storages.GetStorageByIdAsync(id);
            if (storage == null) return Results.Json(Resp<Storage>.Error(404, "storage not found"));
            // Fill MountDetails (Go: makeStorageResp)
            var driver = driverFactory.GetDriver(storage.MountPath);
            if (driver is IDriverWithDetails withDetails)
            {
                try { storage.MountDetails = await withDetails.GetDetailsAsync(); }
                catch { /* ignore */ }
            }
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

        group.MapPost("storage/delete", async (IStorageService storages, uint id) =>
        {
            await storages.DeleteStorageAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("storage/enable", async (IStorageService storages, uint id) =>
        {
            await storages.EnableStorageAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("storage/disable", async (IStorageService storages, uint id) =>
        {
            await storages.DisableStorageAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("storage/load_all", async (IStorageService storages) =>
        {
            await storages.LoadAllStoragesAsync();
            return Results.Json(new Resp<object>(null));
        });

        // === Meta management ===
        group.MapGet("meta/list", async (IMetaService metas, int page = 1, int perPage = 100) =>
        {
            var (metaList, total) = await metas.ListMetasAsync(page, perPage);
            return Results.Json(new Resp<PagedListResp<Meta>>(new PagedListResp<Meta> { Content = metaList, Total = total }));
        });

        group.MapGet("meta/get", async (IMetaService metas, uint id) =>
        {
            var meta = await metas.GetMetaByIdAsync(id);
            if (meta == null) return Results.Json(Resp<Meta>.Error(404, "meta not found"));
            return Results.Json(new Resp<Meta>(meta));
        });

        group.MapPost("meta/create", async (IMetaService metas, Meta req) =>
        {
            // Go: validHide - validate each line of Hide as regex
            var hideError = ValidateHide(req.Hide);
            if (hideError != null) return Results.Json(Resp<object>.Error(400, $"{hideError} is illegal"));
            await metas.CreateMetaAsync(req);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("meta/update", async (IMetaService metas, Meta req) =>
        {
            // Go: validHide - validate each line of Hide as regex
            var hideError = ValidateHide(req.Hide);
            if (hideError != null) return Results.Json(Resp<object>.Error(400, $"{hideError} is illegal"));
            await metas.UpdateMetaAsync(req);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("meta/delete", async (IMetaService metas, uint id) =>
        {
            await metas.DeleteMetaByIdAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        // === Settings management ===
        // Go: group/groups are int query params
        group.MapGet("setting/list", async (ISettingService settings, int? group = null, string? groups = null) =>
        {
            if (!string.IsNullOrEmpty(groups) || group.HasValue)
            {
                var groupList = !string.IsNullOrEmpty(groups)
                    ? groups.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()
                    : [group!.Value];
                return Results.Json(new Resp<List<SettingItem>>(await settings.ListSettingsByGroupsAsync(groupList)));
            }
            return Results.Json(new Resp<List<SettingItem>>(await settings.ListSettingsAsync()));
        });

        // Go: supports key (single) and keys (comma-separated) query params
        group.MapGet("setting/get", async (ISettingService settings, string? key = null, string? keys = null) =>
        {
            if (!string.IsNullOrEmpty(key))
            {
                var item = await settings.GetSettingItemAsync(key);
                if (item == null) return Results.Json(Resp<SettingItem>.Error(404, "setting not found"));
                return Results.Json(new Resp<SettingItem>(item));
            }
            if (!string.IsNullOrEmpty(keys))
            {
                var keyList = keys.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                var items = await settings.GetSettingsInKeysAsync(keyList);
                return Results.Json(new Resp<List<SettingItem>>(items));
            }
            return Results.Json(Resp<object>.Error(400, "key or keys is required"));
        });

        group.MapPost("setting/save", async (ISettingService settings, List<SettingItem> items) =>
        {
            await settings.SaveSettingsAsync(items);
            return Results.Json(new Resp<object>(null));
        });

        // Go: uses query param "key"
        group.MapPost("setting/delete", async (ISettingService settings, string key) =>
        {
            await settings.DeleteSettingAsync(key);
            return Results.Json(new Resp<object>(null));
        });

        // Go: group/groups are int query params
        group.MapPost("setting/default", async (ISettingService settings, int? group = null, string? groups = null) =>
        {
            if (!string.IsNullOrEmpty(groups) || group.HasValue)
            {
                var groupList = !string.IsNullOrEmpty(groups)
                    ? groups.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()
                    : [group!.Value];
                return Results.Json(new Resp<List<SettingItem>>(await settings.DefaultSettingsByGroupsAsync(groupList)));
            }
            return Results.Json(new Resp<List<SettingItem>>(await settings.DefaultSettingsAsync()));
        });

        group.MapPost("setting/reset_token", async (ISettingService settings) =>
        {
            var token = await settings.ResetTokenAsync();
            return Results.Json(new Resp<string>(token));
        });

        // Offline download tool settings
        group.MapPost("setting/set_aria2", async (OfflineDownloadService downloadService, ISettingService settings, SetToolSettingReq req, CancellationToken ct) =>
        {
            if (req.Data is Dictionary<string, object> dict)
            {
                var items = dict.Select(kv => new SettingItem { Key = $"aria2_{kv.Key}", Value = kv.Value?.ToString() ?? "" }).ToList();
                await settings.SaveSettingsAsync(items, ct);
                var result = await downloadService.ReinitToolAsync("aria2", ct);
                return Results.Json(new Resp<object>(result ?? "ok"));
            }
            return Results.Json(Resp<object>.Error(400, "invalid data"));
        });

        group.MapPost("setting/set_qbit", async (OfflineDownloadService downloadService, ISettingService settings, SetToolSettingReq req, CancellationToken ct) =>
        {
            if (req.Data is Dictionary<string, object> dict)
            {
                var items = dict.Select(kv => new SettingItem { Key = $"qbittorrent_{kv.Key}", Value = kv.Value?.ToString() ?? "" }).ToList();
                await settings.SaveSettingsAsync(items, ct);
                var result = await downloadService.ReinitToolAsync("qBittorrent", ct);
                return Results.Json(new Resp<object>(result ?? "ok"));
            }
            return Results.Json(Resp<object>.Error(400, "invalid data"));
        });

        group.MapPost("setting/set_transmission", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_115", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_115_open", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_123_pan", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_123_open", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_pikpak", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunder", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunderx", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        group.MapPost("setting/set_thunder_browser", ([FromBody] SetToolSettingReq req) =>
            Results.Json(Resp<object>.Error(501, "not implemented")));

        // === Sharing management (admin view) ===
        group.MapGet("share/list", async (ISharingService sharings, int page = 1, int perPage = 100) =>
        {
            // Admin sees all sharings
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

        // === Driver info ===
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

        // === Search index management ===
        group.MapPost("index/build", (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            _ = Task.Run(() => search.BuildIndexAsync());
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/update", (ISearchService search, IConfiguration config, [FromBody] UpdateIndexReq req) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            var paths = req.Paths ?? ["/"];
            _ = Task.Run(() => search.UpdateIndexAsync(paths.FirstOrDefault() ?? "/", []));
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/stop", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            await search.StopIndexAsync();
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("index/clear", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            await search.ClearIndexAsync();
            return Results.Json(new Resp<object>(null));
        });

        group.MapGet("index/progress", async (ISearchService search, IConfiguration config) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            return Results.Json(new Resp<IndexProgress>(await search.GetProgressAsync()));
        });

        // === Message management ===
        group.MapPost("message/get", async (IMessageService messages, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<List<Message>>.Error(401, "unauthorized"));
            return Results.Json(new Resp<List<Message>>(await messages.GetMessagesAsync(userId.Value)));
        });

        group.MapPost("message/send", async (IMessageService messages, [FromBody] SendMessageReq req) =>
        {
            await messages.SendMessageAsync(req.UserId, req.Title, req.Content);
            return Results.Json(new Resp<object>(null));
        });

        // === Scan management ===
        group.MapPost("scan/start", async (IScanService scan, ScanReq req, CancellationToken ct) =>
        {
            try
            {
                await scan.StartScanAsync(req.Path ?? "/", req.Limit, ct);
                return Results.Json(new Resp<object>(null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(Resp<object>.Error(400, ex.Message));
            }
        });
        group.MapPost("scan/stop", async (IScanService scan, CancellationToken ct) =>
        {
            try
            {
                await scan.StopScanAsync(ct);
                return Results.Json(new Resp<object>(null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(Resp<object>.Error(400, ex.Message));
            }
        });
        group.MapGet("scan/progress", (IScanService scan) =>
            Results.Json(new Resp<object>(new { obj_count = scan.ScannedCount, is_done = !scan.IsRunning })));

        // === Admin task routes (legacy compatibility) ===
        group.MapGet("task/list", async (ITaskService tasks, string? type = null) =>
            Results.Json(new Resp<List<TaskInfo>>(await tasks.ListTasksAsync(type))));

        group.MapGet("task/info", async (ITaskService tasks, string id) =>
        {
            var task = await tasks.GetTaskAsync(id);
            if (task == null) return Results.Json(Resp<TaskInfo>.Error(404, "task not found"));
            return Results.Json(new Resp<TaskInfo>(task));
        });

        group.MapPost("task/cancel", async (ITaskService tasks, [FromBody] TaskIdReq req) =>
        {
            await tasks.CancelTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("task/retry", async (ITaskService tasks, [FromBody] TaskIdReq req) =>
        {
            await tasks.RetryTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("task/delete", async (ITaskService tasks, [FromBody] TaskIdReq req) =>
        {
            await tasks.DeleteTaskAsync(req.Id);
            return Results.Json(new Resp<object>(null));
        });

        // === Archive management (Go: admin/archive) ===
        group.MapMethods("archive/meta", ["GET", "POST"], async (IFileSystemService fs, [FromBody] ArchiveMetaReq req) =>
        {
            var obj = await fs.GetAsync(req.Path);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "file not found"));
            if (obj.IsDir()) return Results.Json(Resp<object>.Error(400, "path is a directory"));

            var archiveEntries = await FsEndpoints.ReadArchiveEntriesAsync(fs, req.Path, req.Password);
            if (archiveEntries == null)
                return Results.Json(Resp<object>.Error(500, "failed to read archive"));

            var isEncrypted = archiveEntries.Any(e => e.IsEncrypted);
            return Results.Json(new Resp<ArchiveMetaResp>(new ArchiveMetaResp
            {
                Comment = "",
                IsEncrypted = isEncrypted,
                Content = archiveEntries.Select(e => new ArchiveContentResp
                {
                    Name = e.Name,
                    Size = e.Size,
                    IsDir = e.IsDir
                }).ToList()
            }));
        });

        group.MapMethods("archive/list", ["GET", "POST"], async (IFileSystemService fs, [FromBody] ArchiveListReq req) =>
        {
            var obj = await fs.GetAsync(req.Path);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "file not found"));
            if (obj.IsDir()) return Results.Json(Resp<object>.Error(400, "path is a directory"));

            var archiveEntries = await FsEndpoints.ReadArchiveEntriesAsync(fs, req.Path, req.Password);
            if (archiveEntries == null)
                return Results.Json(Resp<object>.Error(500, "failed to read archive"));

            IEnumerable<ArchiveEntryInfo> filtered = archiveEntries;
            if (!string.IsNullOrEmpty(req.InnerPath))
            {
                var prefix = req.InnerPath.TrimStart('/');
                filtered = filtered.Where(e => e.Name.StartsWith(prefix) && e.Name != prefix);
            }

            var entries = filtered.ToList();
            var result = new List<ObjResp>();
            foreach (var entry in entries)
            {
                var relativePath = entry.Name;
                if (!string.IsNullOrEmpty(req.InnerPath))
                {
                    var prefix = req.InnerPath.TrimStart('/');
                    if (relativePath.StartsWith(prefix))
                        relativePath = relativePath[prefix.Length..].TrimStart('/');
                }

                var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                if (parts.Length == 1 || (parts.Length > 1 && !result.Any(r => r.Name == parts[0])))
                {
                    result.Add(new ObjResp
                    {
                        Name = parts[0],
                        Size = parts.Length == 1 ? entry.Size : 0,
                        IsDir = parts.Length > 1,
                        Modified = DateTime.MinValue,
                        Type = parts.Length > 1 ? 1 : 0
                    });
                }
            }

            var total = result.Count;
            if (req.PerPage > 0)
                result = result.Skip((req.Page - 1) * req.PerPage).Take(req.PerPage).ToList();

            return Results.Json(new Resp<ArchiveListResp>(new ArchiveListResp
            {
                Content = result,
                Total = total
            }));
        });

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }

    /// <summary>Go: validHide - validate each line of Hide as regex</summary>
    private static string? ValidateHide(string? hide)
    {
        if (string.IsNullOrEmpty(hide)) return null;
        foreach (var line in hide.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try { _ = new System.Text.RegularExpressions.Regex(line); }
            catch (ArgumentException) { return line; }
        }
        return null;
    }

    private static string SnakeCase(string name) =>
        string.Concat(name.Select((c, i) =>
            i > 0 && (char.IsUpper(c) || char.IsDigit(c)) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()))
            .Replace("__", "_");
}
