using OpenList.Core.Configuration;

namespace OpenList.Core.Interfaces;

/// <summary>
/// LDAP 认证抽象，服务于 <c>/api/auth/login/ldap</c>。
/// 实现类负责用 LDAP 目录校验用户名/密码，并可选地返回额外的属性
/// （如邮箱、显示名）。与 OpenList Go 的 <c>internal/op/auth.go</c> LDAP 登录路径对应。
/// </summary>
public interface ILdapAuthenticator
{
    /// <summary>是否已配置 LDAP URL（即功能是否开启）。</summary>
    bool Enabled { get; }

    /// <summary>
    /// 使用给定凭据尝试一次 bind。成功时返回 true。
    /// 实现类对于错误的凭据应返回 <c>false</c>（而不是抛异常）。
    /// </summary>
    Task<bool> AuthenticateAsync(string username, string password, CancellationToken ct = default);
}
