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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StorageWithDriver : Storage
{
    [JsonPropertyName("driver_name")]
    public string DriverName { get; set; } = string.Empty;
}
