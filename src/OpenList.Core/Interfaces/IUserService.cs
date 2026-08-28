using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 用户管理服务接口，用于用户查询、创建、更新、删除及 SSH 密钥管理等。
/// </summary>
public interface IUserService
{
    /// <summary>按用户 id 获取用户。</summary>
    Task<Resp<User>> GetAsync(long id, CancellationToken ct = default);
    /// <summary>按用户名获取用户。</summary>
    Task<Resp<User>> GetByNameAsync(string username, CancellationToken ct = default);
    /// <summary>按其 SSO 提供方 id 查找用户。由 SSO 回调流程使用。</summary>
    Task<Resp<User>> GetBySsoIdAsync(string ssoId, CancellationToken ct = default);
    /// <summary>获取当前登录用户的公开信息。</summary>
    Task<Resp<UserPublic>> GetCurrentAsync(long userId, CancellationToken ct = default);
    /// <summary>更新当前登录用户的资料。</summary>
    Task<Resp> UpdateCurrentAsync(long userId, UpdateUserReq req, CancellationToken ct = default);
    /// <summary>获取用户列表。</summary>
    Task<Resp<List<UserPublic>>> ListAsync(CancellationToken ct = default);
    /// <summary>创建新用户。</summary>
    Task<Resp<UserPublic>> CreateAsync(CreateUserReq req, CancellationToken ct = default);
    /// <summary>更新指定用户。</summary>
    Task<Resp> UpdateAsync(long id, UpdateUserReq req, CancellationToken ct = default);
    /// <summary>删除指定用户。</summary>
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>取消指定用户的两步验证。</summary>
    Task<Resp> Cancel2FaAsync(long id, CancellationToken ct = default);
    /// <summary>为指定用户设置两步验证密钥。</summary>
    Task<Resp> SetTwoFactorSecretAsync(long id, string secret, CancellationToken ct = default);
    /// <summary>清除指定用户的缓存（当前为占位实现）。</summary>
    Task<Resp> DelCacheAsync(long id, CancellationToken ct = default);
    /// <summary>列出某用户的 SSH 公钥。</summary>
    Task<Resp<List<SshKey>>> ListSshKeysAsync(long userId, CancellationToken ct = default);
    /// <summary>删除指定的 SSH 公钥。</summary>
    Task<Resp> DeleteSshKeyAsync(long keyId, CancellationToken ct = default);
}

/// <summary>
/// 创建用户的请求载荷。
/// </summary>
public sealed class CreateUserReq
{
    /// <summary>用户名。</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>密码。</summary>
    public string Password { get; set; } = string.Empty;
    /// <summary>角色。</summary>
    public UserRole Role { get; set; } = UserRole.General;
    /// <summary>权限位掩码。</summary>
    public int Permission { get; set; }
    /// <summary>用户文件根路径。</summary>
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }
    /// <summary>是否禁用。</summary>
    public bool Disabled { get; set; }
    /// <summary>是否允许 LDAP 登录。</summary>
    [JsonPropertyName("allow_ldap")]
    public bool AllowLdap { get; set; }
}

/// <summary>
/// 更新用户的请求载荷。所有字段均可选，为 null 表示不修改。
/// </summary>
public sealed class UpdateUserReq
{
    /// <summary>目标用户 id。</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>新用户名（可选）。</summary>
    public string? Username { get; set; }
    /// <summary>新密码（可选）。</summary>
    public string? Password { get; set; }
    /// <summary>新角色（可选）。</summary>
    public UserRole? Role { get; set; }
    /// <summary>新权限位掩码（可选）。</summary>
    public int? Permission { get; set; }
    /// <summary>新文件根路径（可选）。</summary>
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }
    /// <summary>是否禁用（可选）。</summary>
    public bool? Disabled { get; set; }
    /// <summary>是否启用两步验证（可选）。</summary>
    [JsonPropertyName("otp")]
    public bool? TwoFactorEnabled { get; set; }
    /// <summary>是否允许 LDAP 登录（可选）。</summary>
    [JsonPropertyName("allow_ldap")]
    public bool? AllowLdap { get; set; }
    /// <summary>SSO 身份标识（可选）。</summary>
    [JsonPropertyName("sso_id")]
    public string? SsoID { get; set; }
}
