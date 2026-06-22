using System.Text.Json;

namespace OpenList.Core.Drivers;

/// <summary>
/// Configuration payload passed to <see cref="Base.ZioDriverBase.InitAsync"/>.
/// <c>Addition</c> is a JSON string of driver-specific fields.
/// </summary>
public sealed class DriverConfig
{
    public string MountPath { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public string Addition { get; set; } = "{}";
    public int Order { get; set; }
    public bool Disabled { get; set; }
    public string? Remark { get; set; }

    public T GetAddition<T>(JsonSerializerOptions? options = null) where T : new()
        => string.IsNullOrWhiteSpace(Addition) ? new T() : JsonSerializer.Deserialize<T>(Addition, options) ?? new T();

    public static DriverConfig FromStorage(Models.Storage s) => new()
    {
        MountPath = s.MountPath.ToString(),
        Driver = s.Driver,
        Addition = s.Addition,
        Order = s.Order,
        Disabled = s.Disabled,
        Remark = s.Remark,
    };
}
