using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IStorageService
{
    Task<Resp<List<StorageWithDriver>>> ListAsync(CancellationToken ct = default);
    Task<Resp<StorageWithDriver>> GetAsync(long id, CancellationToken ct = default);
    Task<Resp<Storage>> CreateAsync(CreateStorageReq req, CancellationToken ct = default);
    Task<Resp> UpdateAsync(long id, CreateStorageReq req, CancellationToken ct = default);
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
    Task<Resp> EnableAsync(long id, CancellationToken ct = default);
    Task<Resp> DisableAsync(long id, CancellationToken ct = default);
    Task<Resp> LoadAllAsync(CancellationToken ct = default);
    Task<Resp<List<DriverInfo>>> ListDriversAsync(CancellationToken ct = default);
    Task<Resp<List<string>>> ListDriverNamesAsync(CancellationToken ct = default);
    Task<Resp<DriverInfo>> GetDriverInfoAsync(string driverName, CancellationToken ct = default);
}

public sealed class CreateStorageReq
{
    public string MountPath { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public string Addition { get; set; } = "{}";
    public int Order { get; set; }
    public bool Disabled { get; set; }
    public string? Remark { get; set; }
}

public sealed class DriverInfo
{
    public string Name { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public bool IsCloudDriver { get; set; }
    public List<DriverConfigField> Config { get; set; } = [];
}

public sealed class DriverConfigField
{
    public string Name { get; set; } = string.Empty;
    public string Help { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? Options { get; set; }
    public Type ValueType { get; set; } = typeof(string);
    public object? Default { get; set; }
}
