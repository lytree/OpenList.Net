namespace OpenList.Core.Models;

public enum SettingGroup
{
    Single = 0,
    Site = 1,
    Style = 2,
    Preview = 3,
    Global = 4,
    OfflineDownload = 5,
    Index = 6,
    SSO = 7,
    LDAP = 8,
    S3 = 9,
    FTP = 10,
    Traffic = 11
}

public enum SettingFlag
{
    Public = 0,
    Private = 1,
    ReadOnly = 2,
    Deprecated = 3
}

public class SettingItem
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Help { get; set; }
    public string? Type { get; set; }
    public string? Options { get; set; }
    public SettingGroup Group { get; set; }
    public SettingFlag Flag { get; set; }
    public uint Index { get; set; }

    public bool IsDeprecated => Flag == SettingFlag.Deprecated;
}
