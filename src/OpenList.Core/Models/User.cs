using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 系统用户实体，映射数据库 x_users 表。
/// <para>
/// 单用户产品：库里至多存在<em>一个</em>内置管理员账号，其余身份只有匿名访客与系统内部身份
/// （扫描 / 刮削 / 索引）。因此不再有角色、权限位与 BasePath ——
/// 「谁能做什么」只由「是否已登录」一个维度决定。
/// </para>
/// </summary>
public sealed class User
{
    /// <summary>
    /// 系统内部身份的 Id（扫描 / 刮削 / 索引 / 自动扫描）。负值，永远不会与真实账号（正数）
    /// 或匿名访客（0）冲突；<see cref="IsAdmin"/> 据此把它当作与管理员同等放行。
    /// </summary>
    public const long SystemUserId = -1;

    /// <summary>创建一个系统内部身份（拥有全量读写权限，但不是任何真实账号）。</summary>
    /// <param name="name">仅用于日志/审计的名字。</param>
    public static User System(string name) => new() { Id = SystemUserId, Username = name };

    /// <summary>
    /// 是否按「已认证」对待：真实管理员（Id &gt; 0）与系统内部身份（Id &lt; 0）都返回 true，
    /// 匿名访客（Id == 0）返回 false。
    /// </summary>
    public bool IsAdmin() => Id != 0;

    /// <summary>是否可查看隐藏文件（只有匿名访客受隐藏规则限制）。</summary>
    public bool CanSeeHides() => Id != 0;

    /// <summary>是否可免密访问受密码保护的目录（只有匿名访客需要输入密码）。</summary>
    public bool CanAccessWithoutPassword() => Id != 0;

    /// <summary>用户主键。</summary>
    public long Id { get; set; }

    /// <summary>用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>明文密码（一般不直接使用）。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>密码哈希值。</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>是否被禁用。</summary>
    public bool Disabled { get; set; }

    /// <summary>是否开启两步验证（OTP）。</summary>
    [JsonPropertyName("otp")]
    public bool TwoFactorEnabled { get; set; }

    /// <summary>两步验证密钥。</summary>
    public string? TwoFactorSecret { get; set; }

    /// <summary>
    /// SSO 身份标识。默认空串而非 null：旧版 EnsureCreated 建的库把该列声明成
    /// <c>INTEGER NOT NULL</c>，写入 null 会触发 SQLite Error 19 让服务启动即崩。
    /// 空串在语义上等价于"未绑定"（Go 那边用 0 表示），且 TEXT/INTEGER 亲和列都能容纳。
    /// </summary>
    [JsonPropertyName("sso_id")]
    public string? SsoID { get; set; } = string.Empty;

    /// <summary>是否允许 LDAP 登录。</summary>
    [JsonPropertyName("allow_ldap")]
    public bool AllowLdap { get; set; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 用户信息的公开子集，用于对外展示，不含任何敏感字段（如密码）。
/// </summary>
public sealed class UserPublic
{
    /// <summary>用户主键。</summary>
    public long Id { get; set; }

    /// <summary>用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>是否被禁用。</summary>
    public bool Disabled { get; set; }

    /// <summary>是否开启两步验证。</summary>
    [JsonPropertyName("otp")]
    public bool TwoFactorEnabled { get; set; }

    /// <summary>
    /// SSO 身份标识。默认空串而非 null：旧版 EnsureCreated 建的库把该列声明成
    /// <c>INTEGER NOT NULL</c>，写入 null 会触发 SQLite Error 19 让服务启动即崩。
    /// 空串在语义上等价于"未绑定"（Go 那边用 0 表示），且 TEXT/INTEGER 亲和列都能容纳。
    /// </summary>
    [JsonPropertyName("sso_id")]
    public string? SsoID { get; set; } = string.Empty;

    /// <summary>是否允许 LDAP 登录。</summary>
    [JsonPropertyName("allow_ldap")]
    public bool AllowLdap { get; set; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
