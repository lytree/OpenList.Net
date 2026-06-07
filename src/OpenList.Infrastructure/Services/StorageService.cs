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

    public async Task<uint> CreateStorageAsync(Storage storage, CancellationToken ct = default)
    {
        storage.Modified = DateTime.UtcNow;
        _db.Storages.Add(storage);
        await _db.SaveChangesAsync(ct);
        return storage.Id;
    }

    public async Task UpdateStorageAsync(Storage storage, CancellationToken ct = default)
    {
        storage.Modified = DateTime.UtcNow;
        _db.Storages.Update(storage);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteStorageAsync(uint id, CancellationToken ct = default)
    {
        var storage = await _db.Storages.FindAsync([id], ct);
        if (storage != null)
        {
            _db.Storages.Remove(storage);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task EnableStorageAsync(uint id, CancellationToken ct = default)
    {
        var storage = await _db.Storages.FindAsync([id], ct);
        if (storage != null)
        {
            storage.Disabled = false;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task DisableStorageAsync(uint id, CancellationToken ct = default)
    {
        var storage = await _db.Storages.FindAsync([id], ct);
        if (storage != null)
        {
            storage.Disabled = true;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task LoadAllStoragesAsync(CancellationToken ct = default)
    {
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
}
