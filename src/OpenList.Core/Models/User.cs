using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 用户角色枚举。数值需与前端/数据库约定一致。
/// </summary>
public enum UserRole : int
{
    /// <summary>普通用户。</summary>
    General = 0,
    /// <summary>游客。</summary>
    Guest = 1,
    /// <summary>管理员。</summary>
    Admin = 2,
}

/// <summary>
/// 系统用户实体，映射数据库 x_users 表。
/// </summary>
public sealed class User
{
    // 权限位掩码的位定义（镜像 Go model/user.go 的 Permission 位注释）：
    //   0: 可查看隐藏文件
    //   1: 可免密访问
    //   2: 可添加离线下载任务
    //   3: 可建目录/上传
    //   4: 可重命名
    //   5: 可移动
    //   6: 可复制
    //   7: 可删除
    //   ...（其余位为 WebDAV / FTP / 归档 / 分享等）

    /// <summary>是否为管理员角色。</summary>
    public bool IsAdmin() => Role == UserRole.Admin;

    /// <summary>是否可查看隐藏文件（权限位 0）。</summary>
    public bool CanSeeHides() => (Permission & 1) == 1;

    /// <summary>是否可免密访问（权限位 1）。</summary>
    public bool CanAccessWithoutPassword() => ((Permission >> 1) & 1) == 1;

    /// <summary>用户主键。</summary>
    public long Id { get; set; }
    /// <summary>用户名。</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>明文密码（一般不直接使用）。</summary>
    public string Password { get; set; } = string.Empty;
    /// <summary>密码哈希值。</summary>
    public string PasswordHash { get; set; } = string.Empty;
    /// <summary>用户角色。</summary>
    public UserRole Role { get; set; } = UserRole.General;
    /// <summary>是否被禁用。</summary>
    public bool Disabled { get; set; }
    /// <summary>权限位掩码。</summary>
    public int Permission { get; set; }
    /// <summary>用户的文件根路径（相对挂载根）。</summary>
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }
    /// <summary>是否开启两步验证（OTP）。</summary>
    [JsonPropertyName("otp")]
    public bool TwoFactorEnabled { get; set; }
    /// <summary>两步验证密钥。</summary>
    public string? TwoFactorSecret { get; set; }
    /// <summary>SSO 身份标识。</summary>
    [JsonPropertyName("sso_id")]
    public string? SsoID { get; set; }
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
    /// <summary>用户角色。</summary>
    public UserRole Role { get; set; }
    /// <summary>是否被禁用。</summary>
    public bool Disabled { get; set; }
    /// <summary>权限位掩码。</summary>
    public int Permission { get; set; }
    /// <summary>用户的文件根路径。</summary>
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }
    /// <summary>是否开启两步验证。</summary>
    [JsonPropertyName("otp")]
    public bool TwoFactorEnabled { get; set; }
    /// <summary>SSO 身份标识。</summary>
    [JsonPropertyName("sso_id")]
    public string? SsoID { get; set; }
    /// <summary>是否允许 LDAP 登录。</summary>
    [JsonPropertyName("allow_ldap")]
    public bool AllowLdap { get; set; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
