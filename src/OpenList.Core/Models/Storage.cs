namespace OpenList.Core.Models;

public class Storage
{
    public long Id { get; set; }
    public string MountPath { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public string Addition { get; set; } = "{}";
    public int Order { get; set; }
    public bool Disabled { get; set; }
    public string? Remark { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StorageWithDriver : Storage
{
    public string DriverName { get; set; } = string.Empty;
}
