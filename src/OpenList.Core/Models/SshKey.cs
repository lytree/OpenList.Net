using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 用户的 SSH 公钥实体，映射数据库 x_sshkeys 表。
/// </summary>
public sealed class SshKey
{
    /// <summary>密钥主键。</summary>
    public long Id { get; set; }
    /// <summary>所属用户主键。</summary>
    public long UserId { get; set; }
    /// <summary>密钥名称/备注。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>SSH 公钥内容。</summary>
    public string PublicKey { get; set; } = string.Empty;
    /// <summary>密钥指纹（格式为 SHA256:base64）。</summary>
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>最后使用时间。</summary>
    public DateTimeOffset LastUsedTime { get; set; }
}
