using OpenList.Core.Drivers;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IStorageService
{
    Task<(List<Storage> Storages, int Total)> ListStoragesAsync(int page, int perPage, CancellationToken ct = default);
    Task<Storage?> GetStorageByIdAsync(uint id, CancellationToken ct = default);
    Task<uint> CreateStorageAsync(Storage storage, CancellationToken ct = default);
    Task UpdateStorageAsync(Storage storage, CancellationToken ct = default);
    Task DeleteStorageAsync(uint id, CancellationToken ct = default);
    Task EnableStorageAsync(uint id, CancellationToken ct = default);
    Task DisableStorageAsync(uint id, CancellationToken ct = default);
    Task LoadAllStoragesAsync(CancellationToken ct = default);
    IDriver? GetDriverByMountPath(string mountPath);
    DriverInfo GetDriverInfo(string driverName);
    List<DriverInfo> ListDriverInfos();
    List<string> ListDriverNames();
}
