using System.Net;

namespace OpenList.Drivers.Cloud;

/// <summary>
/// 所有云驱动共享的 <see cref="HttpClient"/>。
/// <para>
/// 驱动由 <c>MountService</c> 通过无参构造反射创建，拿不到 DI 容器，因此这里使用
/// 进程级单例 —— 与 Go 侧 <c>base.RestyClient</c> 的包级全局客户端做法一致。
/// 复用同一个 <see cref="HttpClient"/> 可避免 socket 耗尽，并共享连接池与 DNS 缓存。
/// </para>
/// </summary>
internal static class CloudHttpClient
{
    /// <summary>进程级共享客户端。请勿调用 <c>Dispose</c>。</summary>
    public static readonly HttpClient Shared = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            // 云盘 API 普遍返回 gzip/br 压缩，自动解压省去手工处理。
            AutomaticDecompression = DecompressionMethods.All,
            // 连接池中的连接定期重建，避免长连接被中间设备静默掐断后持续失败。
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // 死主机快速失败，而不是耗到整体超时。
            ConnectTimeout = TimeSpan.FromSeconds(30),
            // 云盘 API 常用大量并发分片上传，放宽每主机连接上限。
            MaxConnectionsPerServer = 64,
        };

        return new HttpClient(handler)
        {
            // 整体超时设为无限：与 Go 默认行为一致，由各驱动通过 CancellationToken 控制。
            // 大文件上传/下载动辄数十分钟，固定超时会误杀。连接层已有 ConnectTimeout 兜底。
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
