namespace OpenList.Core.Drivers.Base;

/// <summary>
/// Describes a single driver config field (name, type, help, options).
/// Populated from <see cref="DriverConfigItemAttribute"/> via reflection.
/// </summary>
public sealed class DriverItemAttributes
{
    public string Name { get; set; } = string.Empty;
    public string Help { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? Options { get; set; }
    public Type ValueType { get; set; } = typeof(string);
    public object? Default { get; set; }
}
