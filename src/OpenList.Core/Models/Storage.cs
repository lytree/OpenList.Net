using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

public class Storage
{
    public long Id { get; set; }

    [JsonPropertyName("mount_path")]
    public string MountPath { get; set; } = string.Empty;

    public string Driver { get; set; } = string.Empty;
    public string Addition { get; set; } = "{}";
    public int Order { get; set; }
    public bool Disabled { get; set; }
    public string? Remark { get; set; }

    /// <summary>Runtime status of the mounted storage (e.g. "work", "failed"). Mirrors Go <c>model.Storage.Status</c>.</summary>
    public string Status { get; set; } = "work";

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

    /// <summary>Frontend reads <c>modified</c> (ISO string) for the last-modified column.</summary>
    [JsonPropertyName("modified")]
    public DateTimeOffset Modified { get; set; }

    [JsonIgnore]
    public DateTimeOffset CreatedAt { get; set; }
    [JsonIgnore]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StorageWithDriver : Storage
{
    [JsonPropertyName("driver_name")]
    public string DriverName { get; set; } = string.Empty;
}
