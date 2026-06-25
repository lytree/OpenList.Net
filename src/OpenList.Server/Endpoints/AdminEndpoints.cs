using System.Security.Claims;
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
        // group values are the frontend's Group enum (number-as-string).
        g.MapGet("/setting/list", async (string? group, string? groups, ISettingService svc) =>
        {
            var r = await svc.ListAsync();
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(group)) wanted.Add(group);
            if (!string.IsNullOrEmpty(groups))
            {
                foreach (var g in groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    wanted.Add(g);
            }
            if (wanted.Count == 0) return Results.Ok(r);

            var filtered = r.Data.Where(s => s.Group is not null && wanted.Contains(s.Group)).ToList();
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
        g.MapGet("/message/list", async (long user_id, IMessageService svc) =>
            Results.Ok(await svc.GetAsync(user_id)));

        g.MapGet("/message/get", async (long id, IMessageService svc) =>
            Results.Ok(await svc.GetAsync(id)));

        g.MapPost("/message/create", async (CreateMessageReq req, IMessageService svc) =>
            Results.Ok(await svc.SendAsync(req.UserId, req.Title, req.Content)));

        g.MapPost("/message/update", async (long id, IMessageService svc) =>
            Results.Ok(await svc.MarkReadAsync(id)));

        g.MapPost("/message/delete", async (long id, IMessageService svc) =>
            Results.Ok(Resp.Ok())); // TODO: add DeleteAsync to IMessageService if needed

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
public sealed class ScanStartReq { public string? Path { get; set; } public double Limit { get; set; } }
public sealed class IndexBuildReq { public List<string>? Paths { get; set; } public int MaxDepth { get; set; } = -1; }
