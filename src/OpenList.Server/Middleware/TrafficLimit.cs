namespace OpenList.Server.Middleware;

/// <summary>
/// 把 <see cref="RateLimitManager"/> 持有的四个令牌桶贴到真实字节流上的接线辅助。
/// <para>
/// 令牌桶与流包装本身早已存在（见 <see cref="RateLimiter"/> / <see cref="RateLimitedReadStream"/> /
/// <see cref="RateLimitedWriteStream"/>），但此前**没有任何调用点**：设置项
/// <c>max_client_download_speed</c> 等配了也不生效。本类只负责「在哪一层扣令牌」这一个决定，
/// 避免每个端点各写一套。
/// </para>
/// <para>
/// 语义（对齐 Go <c>internal/stream</c>）：
/// <list type="bullet">
/// <item>客户端下载 = 写进 HTTP 响应的字节；</item>
/// <item>客户端上传 = 从 HTTP 请求体读出的字节；</item>
/// <item>服务端下载 = 从上游（云盘代理 / 归档条目）读出的字节；</item>
/// <item>服务端上传 = 推给上游存储的字节（本实现里与客户端上传同源，二者串联）。</item>
/// </list>
/// 不限速时所有方法都是零开销直通，绝不包装流（<see cref="RateLimiter.IsUnlimited"/>）。
/// </para>
/// </summary>
public static class TrafficLimit
{
    /// <summary>按字节扣减令牌；限速器为空或不限速时立即返回。</summary>
    public static ValueTask ThrottleAsync(RateLimiter? limiter, int bytes, CancellationToken ct = default)
        => limiter is null || bytes <= 0 ? ValueTask.CompletedTask : limiter.WaitNAsync(bytes, ct);

    /// <summary>
    /// 包装上传请求体：读到 N 字节后按客户端上传速率等待。不限速时<b>原样返回</b>，
    /// 保持既有读取路径（含同步读、排空逻辑）完全不变。
    /// </summary>
    public static Stream WrapUpload(Stream body, RateLimiter? limiter)
        => limiter is null || limiter.IsUnlimited ? body : new RateLimitedReadStream(body, limiter);

    /// <summary>
    /// 包装读端（归档条目、代理上游）：读 N 字节后等待。用于无法自己写循环的场景
    /// （例如交给 <c>Results.File</c> 输出，框架内部负责拷贝）。
    /// </summary>
    public static Stream LimitRead(Stream inner, RateLimiter? limiter)
        => limiter is null || limiter.IsUnlimited ? inner : new RateLimitedReadStream(inner, limiter);
}
