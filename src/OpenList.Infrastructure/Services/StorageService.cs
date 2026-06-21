using Microsoft.EntityFrameworkCore;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using Zio;

namespace OpenList.Infrastructure.Services;

public class StorageService : IStorageService
{
    private readonly OpenListDbContext _db;
    private readonly ZioMountService _mountService;

    public StorageService(OpenListDbContext db, ZioMountService mountService)
    {
        _db = db;
        _mountService = mountService;
    }

    public async Task<(List<Storage> Storages, int Total)> ListStoragesAsync(int page, int perPage, CancellationToken ct = default)
    {
        var total = await _db.Storages.CountAsync(ct);
        var storages = await _db.Storages.OrderBy(s => s.Order).Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        return (storages, total);
    }

    public async Task<Storage?> GetStorageByIdAsync(uint id, CancellationToken ct = default)
        => await _db.Storages.FindAsync([id], ct);

    /// <summary>
    /// Go: CreateStorage — validate driver, save to DB, init in memory (even on failure), save addition back.
    /// </summary>
    public async Task<uint> CreateStorageAsync(Storage storage, CancellationToken ct = default)
    {
        var driverNames = _mountService.ListDriverNames();
        if (!driverNames.Contains(storage.Driver))
            throw new InvalidOperationException($"no driver named: {storage.Driver}");

        storage.Modified = DateTime.UtcNow;
        storage.MountPath = FixAndCleanPath(storage.MountPath);
        _db.Storages.Add(storage);
        await _db.SaveChangesAsync(ct);

        _mountService.LoadDriver(storage);
        await SaveAdditionBackAsync(storage, ct);

        return storage.Id;
    }

    /// <summary>
    /// Go: UpdateStorage — validate driver unchanged, drop old, re-init with new config.
    /// </summary>
    public async Task UpdateStorageAsync(Storage storage, CancellationToken ct = default)
    {
        var oldStorage = await _db.Storages.FindAsync([storage.Id], ct);
        if (oldStorage == null)
            throw new InvalidOperationException("storage not found");

        if (oldStorage.Driver != storage.Driver)
            throw new InvalidOperationException("driver cannot be changed");

        oldStorage.Modified = DateTime.UtcNow;
        oldStorage.MountPath = FixAndCleanPath(storage.MountPath);
        _db.Storages.Update(oldStorage);
        await _db.SaveChangesAsync(ct);

        if (storage.Disabled) return;

        _mountService.ReloadDriver(oldStorage, storage);
        await SaveAdditionBackAsync(storage, ct);
    }

    /// <summary>
    /// Go: DeleteStorageById — drop driver if not disabled, remove from map, delete from DB.
    /// </summary>
    public async Task DeleteStorageAsync(uint id, CancellationToken ct = default)
    {
        var storage = await _db.Storages.FindAsync([id], ct);
        if (storage == null) return;

        if (!storage.Disabled)
        {
            _mountService.UnloadDriver(storage.MountPath);
        }

        _db.Storages.Remove(storage);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Go: EnableStorage — set disabled=false in DB, then load into memory.
    /// </summary>
    public async Task EnableStorageAsync(uint id, CancellationToken ct = default)
    {
        var storage = await _db.Storages.FindAsync([id], ct);
        if (storage == null) return;

        if (!storage.Disabled)
            throw new InvalidOperationException("this storage have enabled");

        storage.Disabled = false;
        await _db.SaveChangesAsync(ct);

        _mountService.LoadDriver(storage);
        await SaveAdditionBackAsync(storage, ct);
    }

    /// <summary>
    /// Go: DisableStorage — drop driver, set disabled=true, remove from map.
    /// </summary>
    public async Task DisableStorageAsync(uint id, CancellationToken ct = default)
    {
        var storage = await _db.Storages.FindAsync([id], ct);
        if (storage == null) return;

        if (storage.Disabled)
            throw new InvalidOperationException("this storage have disabled");

        _mountService.UnloadDriver(storage.MountPath);

        storage.Disabled = true;
        storage.Status = ZioMountService.StatusDisabled;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Go: LoadAllStorages — drop each existing driver, reload from DB.
    /// </summary>
    public async Task LoadAllStoragesAsync(CancellationToken ct = default)
    {
        foreach (var s in _mountService.GetAllStorages())
        {
            _mountService.UnloadDriver(s.MountPath);
        }

        var storages = await _db.Storages.Where(s => !s.Disabled).ToListAsync(ct);
        foreach (var storage in storages)
        {
            _mountService.LoadDriver(storage);
        }
    }

    public IFileSystem? GetDriverByMountPath(string mountPath) => _mountService.GetFileSystem(mountPath);

    public DriverInfo GetDriverInfo(string driverName) => _mountService.GetDriverInfo(driverName);

    public List<DriverInfo> ListDriverInfos() => _mountService.ListDriverInfos();

    public List<string> ListDriverNames() => _mountService.ListDriverNames();

    /// <summary>
    /// Go: MustSaveDriverStorage — serialize driver addition and save back to DB.
    /// </summary>
    private async Task SaveAdditionBackAsync(Storage storage, CancellationToken ct)
    {
        var driver = _mountService.GetFileSystem(storage.MountPath);
        if (driver == null) return;
        var additionJson = _mountService.SerializeAddition(driver);
        if (additionJson != null && additionJson != storage.Addition)
        {
            storage.Addition = additionJson;
            _db.Storages.Update(storage);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Normalize mount path: replace backslashes, ensure leading /, clean path.
    /// Mirrors Go's utils.FixAndCleanPath.
    /// </summary>
    private static string FixAndCleanPath(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == ".." && stack.Count > 0) stack.Pop();
            else if (seg != "..") stack.Push(seg);
        }
        return "/" + string.Join("/", stack.Reverse());
    }
}
