using Microsoft.EntityFrameworkCore;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class StorageService : IStorageService
{
    private readonly OpenListDbContext _db;
    private readonly IDriverFactory _driverFactory;

    public StorageService(OpenListDbContext db, IDriverFactory driverFactory)
    {
        _db = db;
        _driverFactory = driverFactory;
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
        // Go: check driver first
        var driverNames = _driverFactory.ListDriverNames();
        if (!driverNames.Contains(storage.Driver))
            throw new InvalidOperationException($"no driver named: {storage.Driver}");

        storage.Modified = DateTime.UtcNow;
        storage.MountPath = FixAndCleanPath(storage.MountPath);
        _db.Storages.Add(storage);
        await _db.SaveChangesAsync(ct);

        // Go: initStorage + callStorageHooks("add")
        _driverFactory.LoadDriver(storage);

        // Go: MustSaveDriverStorage — save addition back after init
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

        // Go: driver cannot be changed
        if (oldStorage.Driver != storage.Driver)
            throw new InvalidOperationException("driver cannot be changed");

        storage.Modified = DateTime.UtcNow;
        storage.MountPath = FixAndCleanPath(storage.MountPath);
        _db.Storages.Update(storage);
        await _db.SaveChangesAsync(ct);

        // Go: if disabled, skip driver reload
        if (storage.Disabled) return;

        // Go: Drop + initStorage + callStorageHooks("update")
        _driverFactory.ReloadDriver(oldStorage, storage);

        // Go: MustSaveDriverStorage — save addition back after init
        await SaveAdditionBackAsync(storage, ct);
    }

    /// <summary>
    /// Go: DeleteStorageById — drop driver if not disabled, remove from map, delete from DB.
    /// </summary>
    public async Task DeleteStorageAsync(uint id, CancellationToken ct = default)
    {
        var storage = await _db.Storages.FindAsync([id], ct);
        if (storage == null) return;

        // Go: drop driver if not disabled
        if (!storage.Disabled)
        {
            _driverFactory.UnloadDriver(storage.MountPath);
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

        // Go: check if already enabled
        if (!storage.Disabled)
            throw new InvalidOperationException("this storage have enabled");

        storage.Disabled = false;
        await _db.SaveChangesAsync(ct);

        // Go: LoadStorage — init in memory
        _driverFactory.LoadDriver(storage);

        // Go: MustSaveDriverStorage
        await SaveAdditionBackAsync(storage, ct);
    }

    /// <summary>
    /// Go: DisableStorage — drop driver, set disabled=true, remove from map.
    /// </summary>
    public async Task DisableStorageAsync(uint id, CancellationToken ct = default)
    {
        var storage = await _db.Storages.FindAsync([id], ct);
        if (storage == null) return;

        // Go: check if already disabled
        if (storage.Disabled)
            throw new InvalidOperationException("this storage have disabled");

        // Go: drop driver + delete from storagesMap
        _driverFactory.UnloadDriver(storage.MountPath);

        storage.Disabled = true;
        storage.Status = DriverFactory.StatusDisabled;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Go: LoadAllStorages — drop each existing driver, reload from DB.
    /// </summary>
    public async Task LoadAllStoragesAsync(CancellationToken ct = default)
    {
        // Go: drop all existing drivers first
        foreach (var s in _driverFactory.GetAllStorages())
        {
            _driverFactory.UnloadDriver(s.MountPath);
        }

        var storages = await _db.Storages.Where(s => !s.Disabled).ToListAsync(ct);
        foreach (var storage in storages)
        {
            _driverFactory.LoadDriver(storage);
        }
    }

    public IDriver? GetDriverByMountPath(string mountPath) => _driverFactory.GetDriver(mountPath);

    public DriverInfo GetDriverInfo(string driverName) => _driverFactory.GetDriverInfo(driverName);

    public List<DriverInfo> ListDriverInfos() => _driverFactory.ListDriverInfos();

    public List<string> ListDriverNames() => _driverFactory.ListDriverNames();

    /// <summary>
    /// Go: MustSaveDriverStorage — serialize driver addition and save back to DB.
    /// </summary>
    private async Task SaveAdditionBackAsync(Storage storage, CancellationToken ct)
    {
        var driver = _driverFactory.GetDriver(storage.MountPath);
        if (driver == null) return;
        var additionJson = _driverFactory.SerializeAddition(driver);
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
        // Clean: remove double slashes, trailing slash (except root), resolve . and ..
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == ".." && stack.Count > 0) stack.Pop();
            else if (seg != "..") stack.Push(seg);
        }
        var result = "/" + string.Join("/", stack.Reverse());
        return result;
    }
}
