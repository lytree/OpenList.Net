using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 内存版管理员广播消息。与 OpenList Go 的
/// <c>server/handles/message.go</c> SendAdminMessage / GetAdminMessage 对应。
/// 管理员发布一条消息；已连接的用户轮询 <see cref="GetAsync"/> 接收。
/// 不持久化——消息仅在广播窗口期内存于内存。
/// </summary>
public interface IBroadcastService
{
    /// <summary>管理员广播一条消息。正文是一个自由形式的 { type, content } 载荷。</summary>
    Task<Resp> SendAsync(BroadcastMessage message, CancellationToken ct = default);
    /// <summary>返回最新的一条广播消息；若从未发送则返回空的 200 响应。</summary>
    Task<Resp<BroadcastMessage?>> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// 广播载荷。<c>Type</c> 是供前端消费的渲染提示（如 "string"、"image"）；
/// <c>Content</c> 是原始消息正文。
/// </summary>
public sealed class BroadcastMessage
{
    /// <summary>类型提示，默认 "string"。</summary>
    public string Type { get; set; } = "string";
    /// <summary>消息正文。</summary>
    public object? Content { get; set; }
}
