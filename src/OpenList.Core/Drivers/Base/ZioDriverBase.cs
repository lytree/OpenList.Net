using System.Reflection;
using Zio;
using Zio.FileSystems;

namespace OpenList.Core.Drivers.Base;

/// <summary>
/// 所有 OpenList 存储驱动的基类。封装一个 Zio <see cref="IFileSystem"/>
/// 并暴露供 <c>ZioMountService</c> 自动发现使用的元数据。
/// 具体实现位于 <c>OpenList.Drivers</c>，通过程序集扫描被发现。
/// 采用组合而非继承——驱动通过 <see cref="System"/> 暴露底层的 <see cref="FileSystem"/>，
/// 而不是直接子类化它。
/// </summary>
public abstract class ZioDriverBase : IDisposable, IAsyncDisposable
{
    /// <summary>底层的 Zio 文件系统（由驱动在 <see cref="InitAsync"/> 中设置）。</summary>
    public IFileSystem? System { get; protected set; }

    /// <summary>UI 中展示的驱动类型名（如 "Local"、"S3"）。</summary>
    public abstract string DriverName { get; }

    /// <summary>驱动是否只支持读操作。</summary>
    public virtual bool ReadOnly => false;

    /// <summary>驱动是否需要网络访问（云端驱动）。</summary>
    public virtual bool IsCloudDriver => false;

    /// <summary>驱动是否在本地排序文件（会增加 order_by/order_direction 字段）。</summary>
    public virtual bool LocalSort => false;

    /// <summary>驱动是否仅支持代理访问（强制 native_proxy webdav_policy）。</summary>
    public virtual bool OnlyProxy => false;

    /// <summary>驱动是否禁用缓存（隐藏 cache_expiration 字段）。</summary>
    public virtual bool NoCache => false;

    /// <summary>驱动是否禁用上传。</summary>
    public virtual bool NoUpload => false;

    /// <summary>驱动是否需要消息往返（如下发验证码）。</summary>
    public virtual bool NeedMs => false;

    /// <summary>当 root_folder_* 为空时使用的默认根路径/id。</summary>
    public virtual string DefaultRoot => string.Empty;

    /// <summary>驱动配置 UI 中展示的警示消息（用 '|' 分隔 状态|消息）。</summary>
    public virtual string Alert => string.Empty;

    /// <summary>驱动是否只存储文件索引（如 UrlTree）。</summary>
    public virtual bool OnlyIndices => false;

    /// <summary>驱动是否在有直链时仍优先使用代理下载。</summary>
    public virtual bool PreferProxy => false;

    /// <summary>驱动是否支持范围代理（增加 proxy_range 字段）。</summary>
    public virtual bool ProxyRangeOption => false;

    /// <summary>从 <c>Storage.Addition</c> JSON 解析驱动特有配置并进行初始化。</summary>
    public abstract Task InitAsync(DriverConfig config, CancellationToken ct = default);

    /// <summary>释放驱动资源。</summary>
    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>释放底层文件系统。</summary>
    public virtual void Dispose()
    {
        (System as IDisposable)?.Dispose();
        System = null;
    }

    /// <summary>基于属性的配置架构（通过反射自动收集）。</summary>
    public virtual IEnumerable<DriverItemAttributes> ConfigItems => [];
}

/// <summary>
/// 应用于驱动配置属性的元数据特性，用于描述字段的名称、帮助、是否必填等信息。
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class DriverConfigItemAttribute : Attribute
{
    /// <summary>字段名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>帮助说明。</summary>
    public string Help { get; set; } = string.Empty;
    /// <summary>是否必填。</summary>
    public bool Required { get; set; }
    /// <summary>候选选项（用于选择类字段），可为空。</summary>
    public string? Options { get; set; }
}
