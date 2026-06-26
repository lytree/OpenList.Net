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

    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    [JsonPropertyName("web_proxy")]
    public bool WebProxy { get; set; }

    [JsonPropertyName("webdav_policy")]
    public string? WebdavPolicy { get; set; }

    [JsonPropertyName("enable_sign")]
    public bool EnableSign { get; set; }
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
/// Field names use snake_case to match the frontend's DriverConfig type.
/// </summary>
public sealed class DriverInfoConfig
{
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("local_sort")]
    public bool LocalSort { get; set; }
    [JsonPropertyName("only_local")]
    public bool OnlyLocal { get; set; }
    [JsonPropertyName("only_proxy")]
    public bool OnlyProxy { get; set; }
    [JsonPropertyName("no_cache")]
    public bool NoCache { get; set; }
    [JsonPropertyName("no_upload")]
    public bool NoUpload { get; set; }
    [JsonPropertyName("need_ms")]
    public bool NeedMs { get; set; }
    [JsonPropertyName("default_root")]
    public string DefaultRoot { get; set; } = string.Empty;
    public string? Alert { get; set; }
    [JsonIgnore]
    public bool OnlyIndices { get; set; }
    [JsonIgnore]
    public bool PreferProxy { get; set; }
}
