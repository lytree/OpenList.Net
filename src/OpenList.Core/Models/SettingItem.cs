using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// Setting value type. Serialized as lowercase string to match the frontend
/// <c>Type</c> enum (string/select/bool/text/number/float/multipath).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettingValueType : int
{
    [EnumMember(Value = "string")] String = 0,
    [EnumMember(Value = "number")] Number = 1,
    [EnumMember(Value = "bool")] Bool = 2,
    [EnumMember(Value = "select")] Select = 3,
    [EnumMember(Value = "text")] Text = 4,
    [EnumMember(Value = "float")] Float = 5,
    [EnumMember(Value = "multipath")] MultiPath = 6,
}

/// <summary>
/// Setting visibility/permission flag. Serialized as int to match the frontend
/// <c>Flag</c> enum (0=PUBLIC, 1=PRIVATE, 2=READONLY, 3=DEPRECATED).
/// </summary>
public enum SettingFlag : int
{
    Public = 0,
    Private = 1,
    Readonly = 2,
    Deprecated = 3,
}

/// <summary>
/// Setting group. Numeric values match the frontend's Group enum
/// (0=SINGLE, 1=SITE, 2=STYLE, 3=PREVIEW, 4=GLOBAL, 5=ARIA2, 6=INDEX,
/// 7=SSO, 8=LDAP, 9=S3, 10=FTP, 11=TRAFFIC).
/// </summary>
public enum SettingGroup : int
{
    Single = 0,
    Site = 1,
    Style = 2,
    Preview = 3,
    Global = 4,
    Aria2 = 5,
    Index = 6,
    Sso = 7,
    Ldap = 8,
    S3 = 9,
    Ftp = 10,
    Traffic = 11,
}

public sealed class SettingItem
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Help { get; set; }
    public SettingValueType Type { get; set; }
    public SettingFlag Flag { get; set; }
    public string? Options { get; set; }
    public int Group { get; set; }
}
