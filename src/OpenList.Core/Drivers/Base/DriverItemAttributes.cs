namespace OpenList.Core.Drivers.Base;

/// <summary>
/// 描述单个驱动配置字段（名称、类型、帮助、选项）。
/// 通过反射从 <see cref="DriverConfigItemAttribute"/> 填充。
/// </summary>
public sealed class DriverItemAttributes
{
    /// <summary>字段名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>帮助说明。</summary>
    public string Help { get; set; } = string.Empty;
    /// <summary>是否必填。</summary>
    public bool Required { get; set; }
    /// <summary>候选选项（用于选择类字段），可为空。</summary>
    public string? Options { get; set; }
    /// <summary>字段对应的值类型，默认 string。</summary>
    public Type ValueType { get; set; } = typeof(string);
    /// <summary>默认值，可为空。</summary>
    public object? Default { get; set; }
}
