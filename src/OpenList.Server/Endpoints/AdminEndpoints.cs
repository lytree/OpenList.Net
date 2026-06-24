using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
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

        g.MapPost("/storage/update", async (long id, CreateStorageReq req, IStorageService svc) =>
            Results.Ok(await svc.UpdateAsync(id, req)));

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
        g.MapGet("/user/list", async (IUserService users) =>
            Results.Ok(await users.ListAsync()));

        g.MapGet("/user/get", async (long id, IUserService users) =>
            Results.Ok(await users.GetAsync(id)));

        g.MapPost("/user/create", async (CreateUserReq req, IUserService users) =>
            Results.Ok(await users.CreateAsync(req)));

        g.MapPost("/user/update", async (long id, UpdateUserReq req, IUserService users) =>
            Results.Ok(await users.UpdateAsync(id, req)));

        g.MapPost("/user/cancel_2fa", async (long id, IUserService users) =>
            Results.Ok(await users.Cancel2FaAsync(id)));

        g.MapPost("/user/delete", async (long id, IUserService users) =>
            Results.Ok(await users.DeleteAsync(id)));

        g.MapPost("/user/set_password", async (SetPasswordReq req, IUserService users) =>
            Results.Ok(await users.UpdateAsync(req.Id, new UpdateUserReq { Password = req.Password })));

        // --- Setting management ---
        g.MapGet("/setting/list", async (ISettingService svc) =>
            Results.Ok(await svc.ListAsync()));

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

        // --- Meta management ---
        g.MapPost("/meta/list", async (IMetaService svc) =>
            Results.Ok(await svc.ListAsync()));

        g.MapPost("/meta/get", async (MetaGetReq req, IMetaService svc) =>
            Results.Ok(await svc.GetAsync(req.Path)));

        g.MapPost("/meta/save", async (Meta meta, IMetaService svc) =>
            meta.Id == 0
                ? Results.Ok(await svc.CreateAsync(meta))
                : Results.Ok(await svc.UpdateAsync(meta.Id, meta)));

        g.MapPost("/meta/delete", async (MetaDeleteReq req, IMetaService svc) =>
            Results.Ok(await svc.DeleteAsync(req.Id)));

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
        g.MapPost("/scan/{path}", async (string path, IScanService svc) =>
            Results.Ok(await svc.StartAsync("/" + path.TrimStart('/'))));

        return app;
    }
}

public sealed class SetPasswordReq { public long Id { get; set; } public string Password { get; set; } = ""; }
public sealed class MetaGetReq { public string Path { get; set; } = "/"; }
public sealed class MetaDeleteReq { public long Id { get; set; } }
public sealed class CreateMessageReq { public long UserId { get; set; } public string Title { get; set; } = ""; public string Content { get; set; } = ""; }
