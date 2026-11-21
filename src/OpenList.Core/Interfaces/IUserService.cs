using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 账号服务接口：查询、资料更新、初始管理员创建与密码重置。
/// 单用户产品：库里通常只有一个内置管理员，因此**不提供新建 / 删除**能力；
/// 只保留只读列表与按 id 更新（管理台「用户管理」页只放行「编辑」）。
/// SSH 公钥由 <c>ISshKeyService</c> 负责。
/// </summary>
public interface IUserService
{
    /// <summary>按用户 id 获取用户。</summary>
    Task<Resp<User>> GetAsync(long id, CancellationToken ct = default);
    /// <summary>
    /// 列出全部账号（按 id 升序），返回不含密码哈希的公开视图。
    /// 供管理台「用户管理」页展示 —— 单用户产品里它通常只有一行。
    /// </summary>
    Task<Resp<List<UserPublic>>> ListAsync(CancellationToken ct = default);
    /// <summary>按用户名获取用户。</summary>
    Task<Resp<User>> GetByNameAsync(string username, CancellationToken ct = default);
    /// <summary>
    /// 获取系统的唯一账号（按 Id 升序的第一条）。用于初始化向导判断系统是否已完成初始化。
    /// 不存在时返回 <see cref="RespCodes.NotFound"/>。对应 Go <c>op.GetAdmin</c>。
    /// </summary>
    Task<Resp<User>> GetAdminAsync(CancellationToken ct = default);
    /// <summary>按其 SSO 提供方 id 查找用户。由 SSO 回调流程使用。</summary>
    Task<Resp<User>> GetBySsoIdAsync(string ssoId, CancellationToken ct = default);
    /// <summary>获取当前登录用户的公开信息。</summary>
    Task<Resp<UserPublic>> GetCurrentAsync(long userId, CancellationToken ct = default);
    /// <summary>更新当前登录用户的资料。</summary>
    Task<Resp> UpdateCurrentAsync(long userId, UpdateUserReq req, CancellationToken ct = default);
    /// <summary>
    /// 创建系统的<em>初始</em>管理员，仅供 Web 安装向导
    /// <c>POST /api/public/init/setup</c> 在系统未初始化时调用。
    /// 已存在账号时返回 400，对齐 Go <c>handles.InitSetup</c>。
    /// </summary>
    Task<Resp<UserPublic>> CreateInitialAdminAsync(string username, string password, CancellationToken ct = default);
    /// <summary>更新指定用户。</summary>
    Task<Resp> UpdateAsync(long id, UpdateUserReq req, CancellationToken ct = default);
    /// <summary>
    /// 重置管理员的密码。此操作<em>不需要</em>当前登录用户是自己，也不需要旧密码 ——
    /// 它是运维/桌面端的恢复入口：忘记密码时只能靠这个接口重新取得控制权。
    /// 用户表为空（系统尚未初始化）时返回 <see cref="RespCodes.NotFound"/>。
    /// </summary>
    Task<Resp> ResetAdminPasswordAsync(string newPassword, CancellationToken ct = default);
    /// <summary>为指定用户设置两步验证密钥。</summary>
    Task<Resp> SetTwoFactorSecretAsync(long id, string secret, CancellationToken ct = default);
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
