using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Admin endpoints: storage CRUD, driver listing, user management, settings, meta, messages, scan.
/// All require admin role. Mirrors OpenList Go <c>server/handles/admin</c>.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("admin");

        // --- Storage management ---
        // GET /admin/storage/list -> Resp<PageResp<StorageWithDriver>> = { content, total }
        g.MapGet("/storage/list", async (IStorageService svc) =>
            Results.Ok(await svc.ListAsync()));

        g.MapGet("/storage/get", async (long id, IStorageService svc) =>
            Results.Ok(await svc.GetAsync(id)));

        g.MapPost("/storage/create", async (CreateStorageReq req, IStorageService svc) =>
            Results.Ok(await svc.CreateAsync(req)));

        g.MapPost("/storage/update", async (CreateStorageReq req, IStorageService svc) =>
            Results.Ok(await svc.UpdateAsync(req.Id, req)));

        g.MapPost("/storage/delete", async (long id, IStorageService svc) =>
            Results.Ok(await svc.DeleteAsync(id)));

        g.MapPost("/storage/enable", async (long id, IStorageService svc) =>
            Results.Ok(await svc.EnableAsync(id)));

        g.MapPost("/storage/disable", async (long id, IStorageService svc) =>
            Results.Ok(await svc.DisableAsync(id)));

        g.MapPost("/storage/load_all", async (IStorageService svc) =>
            Results.Ok(await svc.LoadAllAsync()));

        // --- Driver listing ---
        g.MapGet("/driver/list", async (IStorageService svc) =>
            Results.Ok(await svc.ListDriversAsync()));

        g.MapGet("/driver/names", async (IStorageService svc) =>
            Results.Ok(await svc.ListDriverNamesAsync()));

        g.MapGet("/driver/info", async (string driver, IStorageService svc) =>
            Results.Ok(await svc.GetDriverInfoAsync(driver)));

        // --- User management ---
        // GET /admin/user/list -> { content, total } (frontend Users.tsx reads data.content)
        g.MapGet("/user/list", async (IUserService users) =>
        {
            var r = await users.ListAsync();
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data, total = r.Data.Count }));
        });

        g.MapGet("/user/get", async (long id, IUserService users) =>
            Results.Ok(await users.GetAsync(id)));

        g.MapPost("/user/create", async (CreateUserReq req, IUserService users) =>
            Results.Ok(await users.CreateAsync(req)));

        g.MapPost("/user/update", async (UpdateUserReq req, IUserService users) =>
            Results.Ok(await users.UpdateAsync(req.Id, req)));

        g.MapPost("/user/cancel_2fa", async (long id, IUserService users) =>
            Results.Ok(await users.Cancel2FaAsync(id)));

        g.MapPost("/user/delete", async (long id, IUserService users) =>
            Results.Ok(await users.DeleteAsync(id)));

        g.MapPost("/user/set_password", async (SetPasswordReq req, IUserService users) =>
            Results.Ok(await users.UpdateAsync(req.Id, new UpdateUserReq { Password = req.Password })));

        // POST /admin/user/del_cache?id= — clear a user's cached profile (no-op until
        // a cache layer is added, but the route is registered for frontend compat).
        g.MapPost("/user/del_cache", async (long id, IUserService users) =>
            Results.Ok(await users.DelCacheAsync(id)));

        // --- Admin SSH-key management (any user) ---
        // GET /admin/user/sshkey/list?uid={uid} — paged response { content, total }.
        g.MapGet("/user/sshkey/list", async (long uid, ISshKeyService svc) =>
        {
            var r = await svc.ListAsync(uid);
            if (r.Code != 200) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data!, total = r.Data!.Count }));
        });

        // POST /admin/user/sshkey/delete?uid={uid}&id={id} — returns remaining keys.
        g.MapPost("/user/sshkey/delete", async (long uid, long id, ISshKeyService svc) =>
            Results.Ok(await svc.DeleteAsync(uid, id, isAdmin: true)));

        // --- Setting management ---
        // GET /admin/setting/list?group=N  or  ?groups=N1,N2  — filter by group.
        // group values are the frontend's Group enum (numeric).
        g.MapGet("/setting/list", async (string? group, string? groups, ISettingService svc) =>
        {
            var r = await svc.ListAsync();
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);

            var wanted = new HashSet<int>();
            if (!string.IsNullOrEmpty(group) && int.TryParse(group, out var g1)) wanted.Add(g1);
            if (!string.IsNullOrEmpty(groups))
            {
                foreach (var g in groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(g, out var gn)) wanted.Add(gn);
            }
            if (wanted.Count == 0) return Results.Ok(r);

            var filtered = r.Data.Where(s => wanted.Contains(s.Group)).ToList();
            return Results.Ok(Resp<List<SettingItem>>.Ok(filtered));
        });

        g.MapPost("/setting/save", async (List<SettingItem> items, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync(items)));

        g.MapGet("/setting/get", async (string keys, ISettingService svc) =>
        {
            var results = new List<SettingItem>();
            foreach (var key in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var r = await svc.GetAsync(key);
                if (r.Code == 200 && r.Data is not null) results.Add(r.Data);
            }
            return Results.Ok(Resp<List<SettingItem>>.Ok(results));
        });

        g.MapPost("/setting/set", async (List<SettingItem> items, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync(items)));

        // DELETE /admin/setting/delete?key= — remove a single setting key.
        g.MapPost("/setting/delete", async (string key, ISettingService svc) =>
        {
            if (string.IsNullOrEmpty(key)) return Results.Ok(Resp.Fail("key is required", RespCodes.BadRequest));
            return Results.Ok(await svc.DeleteAsync(key));
        });

        // POST /admin/setting/default?group= — restore defaults for a settings group.
        g.MapPost("/setting/default", async (HttpRequest req, ISettingService svc) =>
        {
            var group = req.Query["group"].ToString();
            return Results.Ok(await svc.DefaultAsync(string.IsNullOrEmpty(group) ? null : group));
        });

        // POST /admin/setting/reset_token — rotate the JWT signing secret. All existing
        // tokens are invalidated. The service updates the singleton AppConfiguration
        // (used by JwtService/SignService) and persists the new secret; here we also
        // update the bearer middleware's validation parameters so validation uses the
        // new secret immediately (without an app restart).
        g.MapPost("/setting/reset_token", async (
            ISettingService svc,
            IOptionsMonitor<JwtBearerOptions> bearerOpts,
            JwtService jwt) =>
        {
            var r = await svc.ResetTokenAsync();
            if (r.Code == 200)
                bearerOpts.CurrentValue.TokenValidationParameters = jwt.BuildValidationParameters();
            return Results.Ok(r);
        });

        // --- Offline-download-tool config routes ---
        // Each route mirrors the Go /admin/setting/set_{tool} endpoints used by the
        // frontend's Other.tsx. They accept a tool-specific body and persist the
        // values via SaveAsync under the canonical setting keys.
        g.MapPost("/setting/set_aria2", async (SetAria2Req req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "aria2_uri", Value = req.Uri ?? "" },
                new() { Key = "aria2_secret", Value = req.Secret ?? "" },
            ])));

        g.MapPost("/setting/set_qbit", async (SetQbitReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "qbittorrent_url", Value = req.Url ?? "" },
                new() { Key = "qbittorrent_seedtime", Value = req.Seedtime ?? "" },
            ])));

        g.MapPost("/setting/set_transmission", async (SetTransmissionReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "transmission_uri", Value = req.Uri ?? "" },
                new() { Key = "transmission_seedtime", Value = req.Seedtime ?? "" },
            ])));

        g.MapPost("/setting/set_123_pan", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "123_pan_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_115", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "115_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_115_open", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "115_open_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_123_open", async (Set123OpenReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "123_open_temp_dir", Value = req.TempDir ?? "" },
                new() { Key = "123_open_callback_url", Value = req.CallbackUrl ?? "" },
            ])));

        g.MapPost("/setting/set_pikpak", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "pikpak_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_thunder", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "thunder_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_thunderx", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "thunderx_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_thunder_browser", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "thunder_browser_temp_dir", Value = req.TempDir ?? "" },
            ])));

        // --- Meta management ---
        // Mirrors the frontend's Metas.tsx / AddOrEdit.tsx:
        //   GET    /admin/meta/list              -> { content, total }
        //   GET    /admin/meta/get?id=           -> Meta
        //   POST   /admin/meta/create            -> Meta
        //   POST   /admin/meta/update            -> {}
        //   POST   /admin/meta/delete?id=        -> {}
        g.MapGet("/meta/list", async (IMetaService svc) =>
        {
            var r = await svc.ListAsync();
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data, total = r.Data.Count }));
        });

        g.MapGet("/meta/get", async (long id, IMetaService svc) =>
            Results.Ok(await svc.GetByIdAsync(id)));

        g.MapPost("/meta/create", async (Meta meta, IMetaService svc) =>
            Results.Ok(await svc.CreateAsync(meta)));

        g.MapPost("/meta/update", async (Meta meta, IMetaService svc) =>
            Results.Ok(await svc.UpdateAsync(meta.Id, meta)));

        // Back-compat: /meta/save dispatches create vs update by Id.
        g.MapPost("/meta/save", async (Meta meta, IMetaService svc) =>
            meta.Id == 0
                ? Results.Ok(await svc.CreateAsync(meta))
                : Results.Ok(await svc.UpdateAsync(meta.Id, meta)));

        g.MapPost("/meta/delete", async (long id, IMetaService svc) =>
            Results.Ok(await svc.DeleteAsync(id)));

        // --- Message management ---
        // Persistent admin-to-user messages (CRUD).
        g.MapGet("/message/list", async (long user_id, IMessageService svc) =>
            Results.Ok(await svc.GetAsync(user_id)));

        // GET /admin/message/get?id= — fetch a single persistent message by id.
        g.MapGet("/message/get", async (long id, IMessageService svc) =>
            Results.Ok(await svc.GetByIdAsync(id)));

        g.MapPost("/message/create", async (CreateMessageReq req, IMessageService svc) =>
            Results.Ok(await svc.SendAsync(req.UserId, req.Title, req.Content)));

        // Alias: frontend Messenger uses /admin/message/send with { message } body for
        // admin broadcast (in-memory). Kept alongside /message/create for API compat.
        g.MapPost("/message/send", async (SendBroadcastReq? req, IBroadcastService svc) =>
            Results.Ok(await svc.SendAsync(req?.Message ?? new BroadcastMessage())));

        g.MapPost("/message/update", async (long id, IMessageService svc) =>
            Results.Ok(await svc.MarkReadAsync(id)));

        // POST /admin/message/delete?id= — delete a persistent message.
        g.MapPost("/message/delete", async (long id, IMessageService svc) =>
            Results.Ok(await svc.DeleteAsync(id)));

        // POST /admin/message/get (no body) — retrieve the latest admin broadcast.
        // The frontend Messenger polls this to display site-wide announcements.
        g.MapPost("/message/get", async (IBroadcastService svc) =>
            Results.Ok(await svc.GetAsync()));

        // --- Scan ---
        // --- Scan management ---
        // POST /admin/scan/start — body { path, limit }. limit is a rate limit
        // (files/sec, 0 = unlimited); currently ignored by ScanService but accepted
        // for frontend compatibility.
        g.MapPost("/scan/start", async (ScanStartReq req, IScanService svc) =>
            Results.Ok(await svc.StartAsync(req.Path ?? "/", default)));

        // POST /admin/scan/stop — cancel a running scan.
        g.MapPost("/scan/stop", async (IScanService svc) =>
            Results.Ok(await svc.StopAsync()));

        // GET /admin/scan/progress — returns { obj_count, is_done } shape expected
        // by the frontend. is_done is true when no scan is running.
        g.MapGet("/scan/progress", async (IScanService svc) =>
        {
            var r = await svc.GetProgressAsync();
            if (r.Code != 200) return Results.Ok(r);
            var p = r.Data!;
            return Results.Ok(Resp<object>.Ok(new
            {
                obj_count = p.Scanned,
                is_done = p.State != "running",
            }));
        });

        // --- Search index management ---
        // POST /admin/index/build — body { paths, max_depth }. Full rebuild.
        g.MapPost("/index/build", async (IndexBuildReq req, ISearchService svc) =>
            Results.Ok(await svc.BuildIndexAsync(req.Paths ?? [], req.MaxDepth)));

        // POST /admin/index/update — body { paths, max_depth }. Incremental update.
        g.MapPost("/index/update", async (IndexBuildReq req, ISearchService svc) =>
            Results.Ok(await svc.UpdateIndexAsync(req.Paths ?? [], req.MaxDepth)));

        // POST /admin/index/stop — cancel a running index build.
        g.MapPost("/index/stop", async (ISearchService svc) =>
            Results.Ok(await svc.StopIndexAsync()));

        // POST /admin/index/clear — clear the index and reset progress.
        g.MapPost("/index/clear", async (ISearchService svc) =>
            Results.Ok(await svc.ClearIndexAsync()));

        // GET /admin/index/progress — returns { obj_count, is_done, last_done_time, error }
        // matching the frontend's Progress type.
        g.MapGet("/index/progress", async (ISearchService svc) =>
        {
            var r = await svc.GetProgressAsync();
            if (r.Code != 200) return Results.Ok(r);
            var p = r.Data!;
            return Results.Ok(Resp<object>.Ok(new
            {
                obj_count = p.Indexed,
                is_done = p.State != "running",
                last_done_time = p.LastDoneTime?.UtcDateTime.ToString("O") ?? "",
                error = p.Error ?? "",
            }));
        });

        return app;
    }
}

public sealed class SetPasswordReq { public long Id { get; set; } public string Password { get; set; } = ""; }
public sealed class CreateMessageReq { public long UserId { get; set; } public string Title { get; set; } = ""; public string Content { get; set; } = ""; }
public sealed class SendBroadcastReq { public BroadcastMessage? Message { get; set; } }
public sealed class ScanStartReq { public string? Path { get; set; } public double Limit { get; set; } }
public sealed class IndexBuildReq
{
    public List<string>? Paths { get; set; }
    [JsonPropertyName("max_depth")]
    public int MaxDepth { get; set; } = -1;
}

// --- Offline-download-tool config request DTOs (frontend Other.tsx) ---
public sealed class SetAria2Req { public string? Uri { get; set; } public string? Secret { get; set; } }
public sealed class SetQbitReq { public string? Url { get; set; } public string? Seedtime { get; set; } }
public sealed class SetTransmissionReq { public string? Uri { get; set; } public string? Seedtime { get; set; } }
public sealed class SetTempDirReq { public string? TempDir { get; set; } }
public sealed class Set123OpenReq { public string? TempDir { get; set; } public string? CallbackUrl { get; set; } }
