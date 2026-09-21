namespace OpenList.Core.Drivers;

/// <summary>
/// 驱动把运行时变更后的 <c>Addition</c> 回写到存储记录的通道。
/// <para>
/// 对应 OpenList Go 的 <c>op.MustSaveDriverStorage</c>：云盘类驱动（阿里云盘、115、OneDrive 等）
/// 在访问令牌过期后需要刷新 OAuth token，并把新的 access_token / refresh_token 持久化，
/// 否则进程重启后会掉线。驱动本身不持有数据库依赖，因此由挂载服务
/// （<c>ZioMountService</c>）在调用驱动 <c>InitAsync</c> 之前注入本通道。
/// </para>
/// </summary>
public interface IStorageAdditionSink
{
    /// <summary>
    /// 按挂载路径定位存储，并把 <paramref name="addition"/> 写入其 <c>Addition</c> 字段。
    /// </summary>
    /// <param name="mountPath">存储的挂载路径（<c>Storage.MountPath</c>），用于定位记录。</param>
    /// <param name="addition">新的驱动附加配置 JSON 字符串。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入成功返回 <c>true</c>；未找到对应存储返回 <c>false</c>。</returns>
    Task<bool> SaveAdditionAsync(string mountPath, string addition, CancellationToken ct = default);
}
