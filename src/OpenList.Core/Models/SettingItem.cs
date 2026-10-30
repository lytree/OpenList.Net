using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 设置值的类型。序列化为小写字符串以匹配前端的 <c>Type</c> 枚举
/// （string/select/bool/text/number/float/multipath）。
/// 通过自定义的 JsonStringEnumConverter 与小写命名策略，将 PascalCase 枚举名
/// 转换为前端期望的小写字符串。
/// </summary>
[JsonConverter(typeof(SettingValueTypeConverter))]
public enum SettingValueType : int
{
    /// <summary>字符串。</summary>
    String = 0,
    /// <summary>整数。</summary>
    Number = 1,
    /// <summary>布尔。</summary>
    Bool = 2,
    /// <summary>下拉选择（配合 Options）。</summary>
    Select = 3,
    /// <summary>多行文本。</summary>
    Text = 4,
    /// <summary>浮点数。</summary>
    Float = 5,
    /// <summary>多路径。</summary>
    Multipath = 6,
}

/// <summary>
/// 将 <see cref="SettingValueType"/> 与前端的小写类型字符串
/// （"string","number","bool","select","text","float","multipath"）互转。
/// </summary>
internal sealed class SettingValueTypeConverter : JsonConverter<SettingValueType>
{
    /// <summary>读取时：兼容字符串、忽略大小写解析；也兼容数字形式。</summary>
    public override SettingValueType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 字符串：尝试忽略大小写解析枚举
        if (reader.TokenType == JsonTokenType.String)
        {
            var v = reader.GetString();
            if (v is not null && Enum.TryParse(v, ignoreCase: true, out SettingValueType result))
                return result;
        }
        // 数字：直接按其 int 值转换
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var i))
            return (SettingValueType)i;
        // 无法解析时回退为字符串类型
        return SettingValueType.String;
    }

    /// <summary>写出时：一律转为小写字符串。</summary>
    public override void Write(Utf8JsonWriter writer, SettingValueType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}

/// <summary>
/// 设置的可见性/权限标志。序列化为 int 以匹配前端的 <c>Flag</c> 枚举
/// （0=PUBLIC, 1=PRIVATE, 2=READONLY, 3=DEPRECATED）。
/// </summary>
public enum SettingFlag : int
{
    /// <summary>公开可见。</summary>
    Public = 0,
    /// <summary>私有。</summary>
    Private = 1,
    /// <summary>只读。</summary>
    Readonly = 2,
    /// <summary>已废弃。</summary>
    Deprecated = 3,
}

/// <summary>
/// 设置所属分组。数值与前端 Group 枚举一致
/// （0=SINGLE, 1=SITE, 2=STYLE, 3=PREVIEW, 4=GLOBAL, 5=ARIA2, 6=INDEX,
/// 7=SSO, 8=LDAP, 9=S3, 10=FTP, 11=TRAFFIC）。
/// </summary>
public enum SettingGroup : int
{
    /// <summary>单条。</summary>
    Single = 0,
    /// <summary>站点。</summary>
    Site = 1,
    /// <summary>样式。</summary>
    Style = 2,
    /// <summary>预览。</summary>
    Preview = 3,
    /// <summary>全局。</summary>
    Global = 4,
    /// <summary>Aria2 下载工具。</summary>
    Aria2 = 5,
    /// <summary>搜索索引。</summary>
    Index = 6,
    /// <summary>SSO。</summary>
    Sso = 7,
    /// <summary>LDAP。</summary>
    Ldap = 8,
    /// <summary>S3 网关。</summary>
    S3 = 9,
    /// <summary>FTP。</summary>
    Ftp = 10,
    /// <summary>流量。</summary>
    Traffic = 11,
    /// <summary>媒体库。</summary>
    Media = 12,
    /// <summary>访问控制（媒体 / 首页各自的访客开关）。</summary>
    Access = 13,
}

/// <summary>
/// 单条设置项，表示一个可配置的键值对及其元信息。
/// </summary>
public sealed class SettingItem
{
    /// <summary>设置键名。</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>设置值（字符串形态存储）。</summary>
    public string Value { get; set; } = string.Empty;
    /// <summary>设置帮助说明，可为空。</summary>
    public string? Help { get; set; }
    /// <summary>值的类型。</summary>
    public SettingValueType Type { get; set; }
    /// <summary>可见性/权限标志。</summary>
    public SettingFlag Flag { get; set; }
    /// <summary>选择类设置的候选选项，可为空。</summary>
    public string? Options { get; set; }
    /// <summary>所属分组编号。</summary>
    public int Group { get; set; }
}
