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
    /// <summary>
    /// 获取系统中的首个管理员账号（按 Id 升序）。用于初始化向导判断系统是否已完成初始化。
    /// 不存在时返回 <see cref="RespCodes.NotFound"/>。对应 Go <c>op.GetAdmin</c>。
    /// </summary>
    Task<Resp<User>> GetAdminAsync(CancellationToken ct = default);
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
    /// <summary>
    /// 创建系统的<em>初始</em>管理员，仅供 Web 安装向导
    /// <c>POST /api/public/init/setup</c> 在系统未初始化时调用。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="CreateAsync"/> 的区别是本方法会把账号标记为
    /// <see cref="User.IsBuiltIn"/>，使其此后不可被删除 / 改名 / 降权 —— 它是系统保证存在的
    /// 唯一管理入口。刻意不把该标记做进 <see cref="CreateUserReq"/>：那样已登录的管理员就能
    /// 把任意账号标成"内置"，而这个标记本应全局只有一份。
    /// 已存在管理员时返回 400，对齐 Go <c>handles.InitSetup</c>。判据是"有 admin"而非
    /// "用户表非空" —— 初始化会自动创建 guest，用表判据会让向导在全新库上永远被拒。
    /// </remarks>
    Task<Resp<UserPublic>> CreateInitialAdminAsync(string username, string password, CancellationToken ct = default);
    /// <summary>更新指定用户。</summary>
    Task<Resp> UpdateAsync(long id, UpdateUserReq req, CancellationToken ct = default);
    /// <summary>删除指定用户。</summary>
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>
    /// 重置内置管理员的密码。此操作<em>不需要</em>当前登录用户是自己，也不需要旧密码 ——
    /// 它是运维/桌面端的恢复入口：内置管理员本身不可删除、不可改名，一旦忘记密码
    /// 只能靠这个接口（或管理界面）重新取得控制权。
    /// 用户表为空（系统尚未初始化）时返回 <see cref="RespCodes.NotFound"/>。
    /// </summary>
    Task<Resp> ResetAdminPasswordAsync(string newPassword, CancellationToken ct = default);
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
