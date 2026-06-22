namespace OpenList.Core.Models;

public enum SettingValueType : int
{
    String = 0,
    Number = 1,
    Bool = 2,
    Select = 3,
}

public enum SettingFlag : int
{
    Public = 0,
    Private = 1,
    Readonly = 2,
}

public sealed class SettingItem
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Help { get; set; }
    public SettingValueType Type { get; set; }
    public SettingFlag Flag { get; set; }
    public string? Options { get; set; }
    public string? Group { get; set; }
}
