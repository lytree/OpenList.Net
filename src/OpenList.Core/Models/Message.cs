namespace OpenList.Core.Models;

/// <summary>
/// 管理员发给用户的消息。与 OpenList Go 的 <c>server/model/message.go</c> 对应。
/// </summary>
public sealed class Message
{
    /// <summary>消息主键。</summary>
    public long Id { get; set; }
    /// <summary>接收消息的用户主键。</summary>
    public long UserId { get; set; }
    /// <summary>消息标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>消息正文。</summary>
    public string Content { get; set; } = string.Empty;
    /// <summary>是否已读。</summary>
    public bool Read { get; set; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
