using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IStorageService
{
    Task<Resp<PageResp<StorageWithDriver>>> ListAsync(CancellationToken ct = default);
    Task<Resp<StorageWithDriver>> GetAsync(long id, CancellationToken ct = default);
    Task<Resp<Storage>> CreateAsync(CreateStorageReq req, CancellationToken ct = default);
    Task<Resp> UpdateAsync(long id, CreateStorageReq req, CancellationToken ct = default);
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
    Task<Resp> EnableAsync(long id, CancellationToken ct = default);
    Task<Resp> DisableAsync(long id, CancellationToken ct = default);
    Task<Resp> LoadAllAsync(CancellationToken ct = default);
    Task<Resp<Dictionary<string, DriverInfo>>> ListDriversAsync(CancellationToken ct = default);
    Task<Resp<List<string>>> ListDriverNamesAsync(CancellationToken ct = default);
    Task<Resp<DriverInfo>> GetDriverInfoAsync(string driverName, CancellationToken ct = default);
}

public sealed class CreateStorageReq
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("mount_path")]
    public string MountPath { get; set; } = string.Empty;

    public string Driver { get; set; } = string.Empty;
    public string Addition { get; set; } = "{}";
    public int Order { get; set; }
    public bool Disabled { get; set; }
    public string? Remark { get; set; }
}

/// <summary>
/// Driver metadata returned by <c>/api/admin/driver/list</c>.
/// Mirrors OpenList Go <c>driver.Info</c>: a map keyed by driver name where each
/// value has <see cref="Common"/> fields, <see cref="Additional"/> driver-specific
/// fields, and a <see cref="Config"/> object describing driver capabilities.
/// </summary>
public sealed class DriverInfo
{
    public List<DriverInfoItem> Common { get; set; } = [];
    public List<DriverInfoItem> Additional { get; set; } = [];
    public DriverInfoConfig Config { get; set; } = new();
}

/// <summary>
/// A single config field. All values are strings to match the OpenList Go
/// <c>driver.Item</c> shape consumed by the frontend.
/// </summary>
public sealed class DriverInfoItem
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string Default { get; set; } = string.Empty;
    public string Options { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string Help { get; set; } = string.Empty;
}

/// <summary>
/// Driver capability flags shown in the UI. Mirrors OpenList Go <c>driver.Config</c>.
/// </summary>
public sealed class DriverInfoConfig
{
    public string Name { get; set; } = string.Empty;
    public bool LocalSort { get; set; }
    public bool OnlyProxy { get; set; }
    public bool NoCache { get; set; }
    public bool NoUpload { get; set; }
    public bool NeedMs { get; set; }
    public string DefaultRoot { get; set; } = string.Empty;
    public string Alert { get; set; } = string.Empty;
    public bool OnlyIndices { get; set; }
    public bool PreferProxy { get; set; }
}
