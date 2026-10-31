namespace OpenList.Core.Interfaces;

/// <summary>
/// LDAP 认证抽象，服务于 <c>/api/auth/login/ldap</c>。
/// 实现类负责用 LDAP 目录校验用户名/密码，并可选地返回额外的属性
/// （如邮箱、显示名）。与 OpenList Go 的 <c>internal/op/auth.go</c> LDAP 登录路径对应。
/// </summary>
public interface ILdapAuthenticator
{
    /// <summary>
    /// LDAP 登录当前是否启用。以设置表的 <c>ldap_login_enabled</c> 与服务器地址
    /// （<c>ldap_server</c>，为空时回退 appsettings 的 <c>Ldap:Url</c>）共同判定 ——
    /// 与前端登录页读取的公开设置是同一个口径，避免「设置页开了、实际不可用」。
    /// </summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// 使用给定凭据尝试一次 bind。成功时返回 true。
    /// 实现类对于错误的凭据应返回 <c>false</c>（而不是抛异常）。
    /// </summary>
    Task<bool> AuthenticateAsync(string username, string password, CancellationToken ct = default);
}
