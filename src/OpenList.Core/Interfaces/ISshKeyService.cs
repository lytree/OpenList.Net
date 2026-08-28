using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// SSH 公钥管理服务，支持当前用户的密钥管理以及（管理员）任意用户的密钥管理。
/// 与 Go 中 <c>server/handles/me.go</c> 的 sshkey 路由以及
/// <c>server/handles/admin/user.go</c> 的管理员 sshkey 路由对应。
/// </summary>
public interface ISshKeyService
{
    /// <summary>列出某用户的 SSH 公钥。</summary>
    Task<Resp<List<SshKeyInfo>>> ListAsync(long userId, CancellationToken ct = default);
    /// <summary>为某用户添加 SSH 公钥，计算指纹并返回该密钥信息。</summary>
    Task<Resp<SshKeyInfo>> AddAsync(long userId, string title, string key, CancellationToken ct = default);
    /// <summary>删除公钥并返回剩余密钥列表；isAdmin 表示是否以管理员身份操作。</summary>
    Task<Resp<List<SshKeyInfo>>> DeleteAsync(long userId, long keyId, bool isAdmin, CancellationToken ct = default);
}

/// <summary>
/// 匹配前端 <c>SSHPublicKey</c> 接口（types/sshkey.ts）的 DTO。
/// 字段名使用 snake_case 以匹配前端期望的 JSON。
/// </summary>
public sealed class SshKeyInfo
{
    /// <summary>密钥 id。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>密钥名称/标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>密钥指纹。</summary>
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>添加时间。</summary>
    public string AddedTime { get; set; } = string.Empty;
    /// <summary>最后使用时间。</summary>
    public string LastUsedTime { get; set; } = string.Empty;
}
