using System.Text.Json;

namespace OpenList.Core.Drivers;

/// <summary>
/// 传给 <see cref="Base.DriverBase.InitAsync"/> 的配置载荷。
/// <c>Addition</c> 是驱动特有字段的 JSON 字符串。
/// </summary>
public sealed class DriverConfig
{
    /// <summary>挂载路径。</summary>
    public string MountPath { get; set; } = string.Empty;
    /// <summary>驱动名称。</summary>
    public string Driver { get; set; } = string.Empty;
    /// <summary>驱动的附加 JSON 配置。</summary>
    public string Addition { get; set; } = "{}";
    /// <summary>排序权重。</summary>
    public int Order { get; set; }
    /// <summary>是否禁用。</summary>
    public bool Disabled { get; set; }
    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    /// <summary>将 <see cref="Addition"/> 反序列化为类型 T；为空或失败时返回默认实例。</summary>
    /// <typeparam name="T">附加配置的目标类型。</typeparam>
    /// <returns>反序列化得到的配置对象。</returns>
    public T GetAddition<T>(JsonSerializerOptions? options = null) where T : new()
        => string.IsNullOrWhiteSpace(Addition) ? new T() : JsonSerializer.Deserialize<T>(Addition, options) ?? new T();

    /// <summary>根据 <see cref="Models.Storage"/> 构造一个 DriverConfig。</summary>
    /// <param name="s">存储实体。</param>
    /// <returns>对应的驱动配置。</returns>
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
