using System.Reflection;
using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Vfs;

namespace OpenList.Core.Drivers.Base;

/// <summary>
/// 所有 OpenList 存储驱动的基类。封装一个 <see cref="IFileSystem"/>
/// 并暴露供 <c>MountService</c> 自动发现使用的元数据。
/// 具体实现位于 <c>OpenList.Drivers</c>，通过程序集扫描被发现。
/// 采用组合而非继承——驱动通过 <see cref="System"/> 暴露底层的 <see cref="FileSystem"/>，
/// 而不是直接子类化它。
/// </summary>
public abstract class DriverBase : IDisposable, IAsyncDisposable
{
    /// <summary>底层的 虚拟文件系统（由驱动在 <see cref="InitAsync"/> 中设置）。</summary>
    public IFileSystem? System { get; protected set; }

    /// <summary>
    /// 本驱动实例对应的挂载路径（即 <c>Storage.MountPath</c>）。
    /// 由挂载服务在调用 <see cref="InitAsync"/> 前通过 <see cref="AttachRuntime"/> 注入；
    /// 测试中直接构造的实例该值为空字符串。
    /// </summary>
    public string MountPath { get; private set; } = string.Empty;

    /// <summary>
    /// Addition 回写通道。由挂载服务注入 —— 为空表示当前实例无法持久化配置变更
    /// （例如单元测试里直接 <c>new</c> 出来的驱动）。
    /// </summary>
    public IStorageAdditionSink? AdditionSink { get; private set; }

    /// <summary>
    /// 由挂载服务在调用 <see cref="InitAsync"/> 之前注入运行时上下文。
    /// 驱动无需自行调用；拆成独立方法是为了不破坏已有驱动对
    /// <see cref="InitAsync"/> 的签名实现。
    /// </summary>
    /// <param name="mountPath">该驱动实例挂载到的存储路径。</param>
    /// <param name="additionSink">Addition 回写通道，可为 null。</param>
    public void AttachRuntime(string mountPath, IStorageAdditionSink? additionSink)
    {
        MountPath = mountPath ?? string.Empty;
        AdditionSink = additionSink;
    }

    /// <summary>
    /// 把 <paramref name="addition"/> 序列化后回写到所属存储的 <c>Addition</c> 字段，
    /// 用于持久化 OAuth token 等运行时变更。对应 Go 的 <c>op.MustSaveDriverStorage</c>。
    /// </summary>
    /// <typeparam name="T">驱动附加配置类型。</typeparam>
    /// <param name="addition">要持久化的附加配置对象。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功写入返回 <c>true</c>；无回写通道、未定位到存储或对象为空时返回 <c>false</c>。</returns>
    /// <remarks>
    /// 序列化按对象的**运行时类型**进行，因此基类以 <c>object</c> 静态类型持有引用时
    /// （例如 <c>CloudDriverBase</c>）也能正确写出全部字段；属性名沿用驱动 Addition 上的
    /// <c>[JsonPropertyName]</c> 标记，与 <see cref="DriverConfig.GetAddition{T}"/> 互为逆操作。
    /// </remarks>
    public Task<bool> SaveAdditionAsync<T>(T addition, CancellationToken ct = default)
    {
        if (addition is null) return Task.FromResult(false);
        return SaveAdditionAsync(JsonSerializer.Serialize(addition, addition.GetType()), ct);
    }

    /// <summary>
    /// 把已序列化的 JSON 回写到所属存储的 <c>Addition</c> 字段。
    /// </summary>
    /// <param name="additionJson">新的附加配置 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功写入返回 <c>true</c>；无回写通道或未定位到存储时返回 <c>false</c>。</returns>
    public async Task<bool> SaveAdditionAsync(string additionJson, CancellationToken ct = default)
    {
        if (AdditionSink is null || string.IsNullOrWhiteSpace(MountPath)) return false;
        return await AdditionSink.SaveAdditionAsync(MountPath, additionJson, ct);
    }

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
