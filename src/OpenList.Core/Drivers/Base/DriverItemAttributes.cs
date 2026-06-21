namespace OpenList.Core.Drivers.Base;

/// <summary>
/// Specifies the options for a select-type driver item
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DriverItemOptionsAttribute : Attribute
{
    public string Options { get; }
    public DriverItemOptionsAttribute(string options) => Options = options;
}

/// <summary>
/// Specifies the type override for a driver item (e.g., "select", "text", "number")
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DriverItemTypeAttribute : Attribute
{
    public string Type { get; }
    public DriverItemTypeAttribute(string type) => Type = type;
}
