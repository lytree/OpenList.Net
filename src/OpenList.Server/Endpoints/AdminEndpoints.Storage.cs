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
        // Go: ListStorages — fetches mount details concurrently with 3s timeout
        group.MapGet("storage/list", async (IStorageService storages, IDriverFactory driverFactory, ISettingService settings, int page = 1, int perPage = 100) =>
        {
            var (storageList, total) = await storages.ListStoragesAsync(page, perPage);
            var hideDetails = settings.GetBool("hide_storage_details_in_manage_page");
            if (!hideDetails)
            {
                // Go: makeStorageResp — fetch details concurrently with 3s timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var tasks = storageList.Select(async s =>
                {
                    var driver = driverFactory.GetDriver(s.MountPath);
                    if (driver is IDriverWithDetails withDetails)
                    {
                        try { s.MountDetails = await withDetails.GetDetailsAsync(cts.Token); }
                        catch { /* ignore errors for storage details */ }
                    }
                }).ToArray();
                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException) { /* timeout is OK */ }
            }
            return Results.Json(new Resp<PagedListResp<Storage>>(new PagedListResp<Storage> { Content = storageList, Total = total }));
        }).WithSummary("获取存储列表");

        // Go: GetStorage — returns raw storage without mount details
        group.MapGet("storage/get", async (IStorageService storages, uint id) =>
        {
            var storage = await storages.GetStorageByIdAsync(id);
            if (storage == null) return Results.Json(Resp<Storage>.Error(404, "storage not found"));
            return Results.Json(new Resp<Storage>(storage));
        }).WithSummary("获取存储详情");

        group.MapPost("storage/create", async (IStorageService storages, Storage req) =>
        {
            var id = await storages.CreateStorageAsync(req);
            return Results.Json(new Resp<IdResp>(new IdResp { Id = id }));
        }).WithSummary("创建存储");

        group.MapPost("storage/update", async (IStorageService storages, Storage req) =>
        {
            await storages.UpdateStorageAsync(req);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("更新存储");

        group.MapPost("storage/delete", async (IStorageService storages, uint id) =>
        {
            await storages.DeleteStorageAsync(id);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("删除存储");

        group.MapPost("storage/enable", async (IStorageService storages, uint id) =>
        {
            await storages.EnableStorageAsync(id);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("启用存储");

        group.MapPost("storage/disable", async (IStorageService storages, uint id) =>
        {
            await storages.DisableStorageAsync(id);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("禁用存储");

        group.MapPost("storage/load_all", async (IStorageService storages) =>
        {
            // Go: LoadAllStorages — async reload in background, return immediately
            _ = Task.Run(async () =>
            {
                try { await storages.LoadAllStoragesAsync(); }
                catch { /* logged internally */ }
            });
            return Results.Json(new Resp<object>(null));
        }).WithSummary("重新加载所有存储");
    }

    private static void MapMetaAdmin(RouteGroupBuilder group)
    {
        group.MapGet("meta/list", async (IMetaService metas, int page = 1, int perPage = 100) =>
        {
            var (metaList, total) = await metas.ListMetasAsync(page, perPage);
            return Results.Json(new Resp<PagedListResp<Meta>>(new PagedListResp<Meta> { Content = metaList, Total = total }));
        }).WithSummary("获取 Meta 列表");

        group.MapGet("meta/get", async (IMetaService metas, uint id) =>
        {
            var meta = await metas.GetMetaByIdAsync(id);
            if (meta == null) return Results.Json(Resp<Meta>.Error(404, "meta not found"));
            return Results.Json(new Resp<Meta>(meta));
        }).WithSummary("获取 Meta 详情");

        group.MapPost("meta/create", async (IMetaService metas, Meta req) =>
        {
            var hideError = ValidateHide(req.Hide);
            if (hideError != null) return Results.Json(Resp<object>.Error(400, $"{hideError} is illegal"));
            await metas.CreateMetaAsync(req);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("创建 Meta");

        group.MapPost("meta/update", async (IMetaService metas, Meta req) =>
        {
            var hideError = ValidateHide(req.Hide);
            if (hideError != null) return Results.Json(Resp<object>.Error(400, $"{hideError} is illegal"));
            await metas.UpdateMetaAsync(req);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("更新 Meta");

        group.MapPost("meta/delete", async (IMetaService metas, uint id) =>
        {
            await metas.DeleteMetaByIdAsync(id);
            return Results.Json(new Resp<object>(null));
        }).WithSummary("删除 Meta");
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
