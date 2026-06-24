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

    public async Task<Resp<List<StorageWithDriver>>> ListAsync(CancellationToken ct = default)
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
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            DriverName = _mount.GetDriverName(s.Driver),
        }).ToList();
        return Resp<List<StorageWithDriver>>.Ok(result);
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

    public Task<Resp<List<DriverInfo>>> ListDriversAsync(CancellationToken ct = default)
    {
        var infos = _mount.DiscoverDriverTypes()
            .Select(t => Activator.CreateInstance(t) as ZioDriverBase)
            .Where(d => d is not null)
            .Select(d => new DriverInfo
            {
                Name = d!.DriverName,
                ReadOnly = d.ReadOnly,
                IsCloudDriver = d.IsCloudDriver,
                Config = d.ConfigItems.Select(c => new DriverConfigField
                {
                    Name = c.Name,
                    Help = c.Help,
                    Required = c.Required,
                    Options = c.Options,
                    ValueType = c.ValueType,
                    Default = c.Default,
                }).ToList(),
            }).ToList();
        return Task.FromResult(Resp<List<DriverInfo>>.Ok(infos));
    }

    public Task<Resp<List<string>>> ListDriverNamesAsync(CancellationToken ct = default)
    {
        var names = _mount.DiscoverDriverTypes()
            .Select(t => Activator.CreateInstance(t) as ZioDriverBase)
            .Where(d => d is not null)
            .Select(d => d!.DriverName)
            .ToList();
        return Task.FromResult(Resp<List<string>>.Ok(names));
    }

    public Task<Resp<DriverInfo>> GetDriverInfoAsync(string driverName, CancellationToken ct = default)
    {
        var type = _mount.DiscoverDriverTypes()
            .FirstOrDefault(t => (Activator.CreateInstance(t) as ZioDriverBase)?.DriverName.Equals(driverName, StringComparison.OrdinalIgnoreCase) == true);
        if (type is null) return Task.FromResult(Resp<DriverInfo>.Fail("driver not found", RespCodes.NotFound));
        var d = (ZioDriverBase)Activator.CreateInstance(type)!;
        return Task.FromResult(Resp<DriverInfo>.Ok(new DriverInfo
        {
            Name = d.DriverName,
            ReadOnly = d.ReadOnly,
            IsCloudDriver = d.IsCloudDriver,
            Config = d.ConfigItems.Select(c => new DriverConfigField
            {
                Name = c.Name,
                Help = c.Help,
                Required = c.Required,
                Options = c.Options,
                ValueType = c.ValueType,
                Default = c.Default,
            }).ToList(),
        }));
    }

    private static string? Validate(CreateStorageReq req)
    {
        if (string.IsNullOrWhiteSpace(req.MountPath)) return "mount path required";
        if (string.IsNullOrWhiteSpace(req.Driver)) return "driver required";
        if (!req.MountPath.StartsWith('/')) return "mount path must start with '/'";
        return null;
    }
}
