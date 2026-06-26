using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// Setting value type. Serialized as lowercase string to match the frontend
/// <c>Type</c> enum (string/select/bool/text/number/float/multipath).
/// Uses a custom JsonStringEnumConverter with a lowercase naming policy so
/// PascalCase enum names become the lowercase strings the frontend expects.
/// </summary>
[JsonConverter(typeof(SettingValueTypeConverter))]
public enum SettingValueType : int
{
    String = 0,
    Number = 1,
    Bool = 2,
    Select = 3,
    Text = 4,
    Float = 5,
    Multipath = 6,
}

/// <summary>
/// Converts <see cref="SettingValueType"/> to the frontend's lowercase Type
/// strings ("string","number","bool","select","text","float","multipath").
/// </summary>
internal sealed class SettingValueTypeConverter : JsonConverter<SettingValueType>
{
    public override SettingValueType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var v = reader.GetString();
            if (v is not null && Enum.TryParse(v, ignoreCase: true, out SettingValueType result))
                return result;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var i))
            return (SettingValueType)i;
        return SettingValueType.String;
    }

    public override void Write(Utf8JsonWriter writer, SettingValueType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
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
