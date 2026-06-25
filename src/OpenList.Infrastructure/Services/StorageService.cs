using Microsoft.EntityFrameworkCore;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Storage CRUD + driver loading. Mirrors OpenList Go <c>internal/op/storage.go</c>.
/// Driver instantiation is delegated to <see cref="ZioMountService"/>.
/// </summary>
public sealed class StorageService : IStorageService
{
    private readonly OpenListDbContext _db;
    private readonly ZioMountService _mount;
    private readonly TimeProvider _time;

    public StorageService(OpenListDbContext db, ZioMountService mount, TimeProvider? time = null)
    {
        _db = db;
        _mount = mount;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Resp<PageResp<StorageWithDriver>>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.Storages.AsNoTracking()
            .OrderBy(s => s.Order).ThenBy(s => s.Id)
            .ToListAsync(ct);
        var result = list.Select(s => new StorageWithDriver
        {
            Id = s.Id,
            MountPath = s.MountPath,
            Driver = s.Driver,
            Addition = s.Addition,
            Order = s.Order,
            Disabled = s.Disabled,
            Remark = s.Remark,
            Status = s.Status,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            DriverName = _mount.GetDriverName(s.Driver),
        }).ToList();
        var page = new PageResp<StorageWithDriver> { Content = result, Total = result.Count };
        return Resp<PageResp<StorageWithDriver>>.Ok(page);
    }

    public async Task<Resp<StorageWithDriver>> GetAsync(long id, CancellationToken ct = default)
    {
        var s = await _db.Storages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Resp<StorageWithDriver>.Fail("storage not found", RespCodes.NotFound);
        return Resp<StorageWithDriver>.Ok(new StorageWithDriver
        {
            Id = s.Id,
            MountPath = s.MountPath,
            Driver = s.Driver,
            Addition = s.Addition,
            Order = s.Order,
            Disabled = s.Disabled,
            Remark = s.Remark,
            Status = s.Status,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            DriverName = _mount.GetDriverName(s.Driver),
        });
    }

    public async Task<Resp<Storage>> CreateAsync(CreateStorageReq req, CancellationToken ct = default)
    {
        var v = Validate(req);
        if (v is not null) return Resp<Storage>.Fail(v, RespCodes.BadRequest);

        if (await _db.Storages.AnyAsync(s => s.MountPath == req.MountPath, ct))
            return Resp<Storage>.Fail("mount path already exists", RespCodes.Conflict);

        var now = _time.GetUtcNow();
        var s = new Storage
        {
            MountPath = req.MountPath,
            Driver = req.Driver,
            Addition = req.Addition,
            Order = req.Order,
            Disabled = req.Disabled,
            Remark = req.Remark,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Storages.Add(s);
        await _db.SaveChangesAsync(ct);

        if (!s.Disabled) await _mount.LoadAsync(s, ct);
        return Resp<Storage>.Ok(s);
    }

    public async Task<Resp> UpdateAsync(long id, CreateStorageReq req, CancellationToken ct = default)
    {
        var v = Validate(req);
        if (v is not null) return Resp.Fail(v, RespCodes.BadRequest);

        var s = await _db.Storages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Resp.Fail("storage not found", RespCodes.NotFound);

        if (await _db.Storages.AnyAsync(x => x.MountPath == req.MountPath && x.Id != id, ct))
            return Resp.Fail("mount path already exists", RespCodes.Conflict);

        await _mount.UnloadAsync(s.MountPath, ct);
        s.MountPath = req.MountPath;
        s.Driver = req.Driver;
        s.Addition = req.Addition;
        s.Order = req.Order;
        s.Disabled = req.Disabled;
        s.Remark = req.Remark;
        s.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        if (!s.Disabled) await _mount.LoadAsync(s, ct);
        return Resp.Ok();
    }

    public async Task<Resp> DeleteAsync(long id, CancellationToken ct = default)
    {
        var s = await _db.Storages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Resp.Fail("storage not found", RespCodes.NotFound);
        await _mount.UnloadAsync(s.MountPath, ct);
        _db.Storages.Remove(s);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public async Task<Resp> EnableAsync(long id, CancellationToken ct = default)
    {
        var s = await _db.Storages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Resp.Fail("storage not found", RespCodes.NotFound);
        s.Disabled = false;
        s.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        await _mount.LoadAsync(s, ct);
        return Resp.Ok();
    }

    public async Task<Resp> DisableAsync(long id, CancellationToken ct = default)
    {
        var s = await _db.Storages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Resp.Fail("storage not found", RespCodes.NotFound);
        await _mount.UnloadAsync(s.MountPath, ct);
        s.Disabled = true;
        s.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public async Task<Resp> LoadAllAsync(CancellationToken ct = default)
    {
        var storages = await _db.Storages.AsNoTracking().Where(s => !s.Disabled).ToListAsync(ct);
        await _mount.LoadAllAsync(storages, ct);
        return Resp.Ok();
    }

    public Task<Resp<Dictionary<string, DriverInfo>>> ListDriversAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, DriverInfo>(StringComparer.Ordinal);
        foreach (var d in EnumerateDrivers())
        {
            map[d.DriverName] = BuildDriverInfo(d);
        }
        return Task.FromResult(Resp<Dictionary<string, DriverInfo>>.Ok(map));
    }

    public Task<Resp<List<string>>> ListDriverNamesAsync(CancellationToken ct = default)
    {
        var names = EnumerateDrivers().Select(d => d.DriverName).ToList();
        return Task.FromResult(Resp<List<string>>.Ok(names));
    }

    public Task<Resp<DriverInfo>> GetDriverInfoAsync(string driverName, CancellationToken ct = default)
    {
        var d = EnumerateDrivers().FirstOrDefault(x => x.DriverName.Equals(driverName, StringComparison.OrdinalIgnoreCase));
        if (d is null) return Task.FromResult(Resp<DriverInfo>.Fail("driver not found", RespCodes.NotFound));
        return Task.FromResult(Resp<DriverInfo>.Ok(BuildDriverInfo(d)));
    }

    private IEnumerable<ZioDriverBase> EnumerateDrivers() => _mount.DiscoverDriverTypes()
        .Select(t => Activator.CreateInstance(t) as ZioDriverBase)
        .Where(d => d is not null)
        .Select(d => d!)!;

    /// <summary>
    /// Builds the <see cref="DriverInfo"/> (common + additional + config) for a driver.
    /// Mirrors OpenList Go <c>op.getMainItems</c> + <c>op.getAdditionalItems</c>.
    /// </summary>
    private static DriverInfo BuildDriverInfo(ZioDriverBase d)
    {
        return new DriverInfo
        {
            Common = BuildCommonItems(d),
            Additional = BuildAdditionalItems(d),
            Config = new DriverInfoConfig
            {
                Name = d.DriverName,
                LocalSort = d.LocalSort,
                OnlyProxy = d.OnlyProxy,
                NoCache = d.NoCache,
                NoUpload = d.NoUpload,
                NeedMs = d.NeedMs,
                DefaultRoot = d.DefaultRoot,
                Alert = d.Alert,
                OnlyIndices = d.OnlyIndices,
                PreferProxy = d.PreferProxy,
            },
        };
    }

    private static List<DriverInfoItem> BuildCommonItems(ZioDriverBase d)
    {
        var items = new List<DriverInfoItem>
        {
            new() { Name = "mount_path", Type = "string", Required = true, Help = "The path you want to mount to, it is unique and cannot be repeated" },
            new() { Name = "order", Type = "number", Help = "use to sort" },
            new() { Name = "remark", Type = "text" },
        };

        if (!d.NoCache)
        {
            items.Add(new() { Name = "cache_expiration", Type = "number", Default = "30", Required = true, Help = "The cache expiration time for this storage" });
            items.Add(new() { Name = "custom_cache_policies", Type = "text", Default = "", Help = "The cache expiration rules for this storage" });
        }

        var mustProxy = d.OnlyProxy;
        if (mustProxy)
        {
            items.Add(new() { Name = "webdav_policy", Type = "select", Default = "native_proxy", Options = "use_proxy_url,native_proxy", Required = true });
        }
        else
        {
            var defaultProxy = d.PreferProxy;
            items.Add(new() { Name = "web_proxy", Type = "bool", Default = defaultProxy ? "true" : "" });
            items.Add(new()
            {
                Name = "webdav_policy",
                Type = "select",
                Options = "302_redirect,use_proxy_url,native_proxy",
                Default = defaultProxy ? "native_proxy" : "302_redirect",
                Required = true,
            });
            if (d.ProxyRangeOption)
            {
                items.Add(new() { Name = "proxy_range", Type = "bool", Help = "Need to enable proxy" });
            }
        }

        items.Add(new() { Name = "down_proxy_url", Type = "text" });
        items.Add(new() { Name = "disable_proxy_sign", Type = "bool", Default = "false", Help = "Disable sign for Download proxy URL" });

        if (d.LocalSort)
        {
            items.Add(new() { Name = "order_by", Type = "select", Options = "name,size,modified" });
            items.Add(new() { Name = "order_direction", Type = "select", Options = "asc,desc" });
        }

        items.Add(new() { Name = "extract_folder", Type = "select", Options = "front,back" });
        items.Add(new() { Name = "disable_index", Type = "bool", Default = "false", Required = true });
        items.Add(new() { Name = "enable_sign", Type = "bool", Default = "false", Required = true });
        return items;
    }

    private static List<DriverInfoItem> BuildAdditionalItems(ZioDriverBase d)
    {
        var items = new List<DriverInfoItem>();
        foreach (var c in d.ConfigItems)
        {
            var item = new DriverInfoItem
            {
                Name = c.Name,
                Type = MapTypeName(c.ValueType, c.Options),
                Default = c.Default?.ToString() ?? string.Empty,
                Options = c.Options ?? string.Empty,
                Required = c.Required,
                Help = c.Help,
            };
            // root_folder_* defaults to DefaultRoot and becomes required when a default exists.
            if ((item.Name == "root_folder_id" || item.Name == "root_folder_path") && string.IsNullOrEmpty(item.Default))
            {
                item.Default = d.DefaultRoot;
                item.Required = !string.IsNullOrEmpty(d.DefaultRoot);
            }
            items.Add(item);
        }
        return items;
    }

    /// <summary>Maps a CLR <see cref="Type"/> to the OpenList frontend type string.</summary>
    private static string MapTypeName(Type t, string? options)
    {
        if (!string.IsNullOrEmpty(options)) return "select";
        var code = Type.GetTypeCode(t);
        return code switch
        {
            TypeCode.Boolean => "bool",
            TypeCode.Int32 or TypeCode.Int64 or TypeCode.Int16 or TypeCode.Byte => "number",
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "float",
            _ => "string",
        };
    }

    private static string? Validate(CreateStorageReq req)
    {
        if (string.IsNullOrWhiteSpace(req.MountPath)) return "mount path required";
        if (string.IsNullOrWhiteSpace(req.Driver)) return "driver required";
        if (!req.MountPath.StartsWith('/')) return "mount path must start with '/'";
        return null;
    }
}
