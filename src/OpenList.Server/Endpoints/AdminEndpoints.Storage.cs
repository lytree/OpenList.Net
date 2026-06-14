using System.Text.RegularExpressions;
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
    private static void MapStorageAdmin(RouteGroupBuilder group)
    {
        group.MapGet("storage/list", async (IStorageService storages, IDriverFactory driverFactory, ISettingService settings, int page = 1, int perPage = 100) =>
        {
            var (storageList, total) = await storages.ListStoragesAsync(page, perPage);
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
    }

    private static void MapMetaAdmin(RouteGroupBuilder group)
    {
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
            var hideError = ValidateHide(req.Hide);
            if (hideError != null) return Results.Json(Resp<object>.Error(400, $"{hideError} is illegal"));
            await metas.CreateMetaAsync(req);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("meta/update", async (IMetaService metas, Meta req) =>
        {
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
    }

    private static string? ValidateHide(string? hide)
    {
        if (string.IsNullOrEmpty(hide)) return null;
        foreach (var line in hide.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try { _ = new Regex(line); }
            catch (ArgumentException) { return line; }
        }
        return null;
    }
}
