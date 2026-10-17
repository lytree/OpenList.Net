using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 认证服务：登录（密码/哈希/LDAP/SSO）、JWT 签发与校验、两步验证、退出登录。
/// 与 OpenList Go 的 <c>server/handles/auth.go</c> 对应。
/// </summary>
public interface IAuthService
{
    /// <summary>用户名+密码登录，可选两步验证码，返回 JWT。</summary>
    Task<Resp<string>> LoginAsync(string username, string password, string? otpCode = null, CancellationToken ct = default);
    /// <summary>用户名+哈希登录，可选两步验证码，返回 JWT。</summary>
    Task<Resp<string>> LoginHashAsync(string username, string passwordHash, string? otpCode = null, CancellationToken ct = default);
    /// <summary>LDAP 登录，返回 JWT。</summary>
    Task<Resp<string>> LoginLdapAsync(string username, string password, CancellationToken ct = default);
    /// <summary>为指定用户生成两步验证密钥/二维码数据。</summary>
    Task<Resp<string>> Generate2FaAsync(long userId, CancellationToken ct = default);
    /// <summary>校验指定用户的两步验证码。</summary>
    Task<Resp> Verify2FaAsync(long userId, string code, CancellationToken ct = default);
    /// <summary>退出登录（使给定 token 失效）。</summary>
    Task<Resp> LogoutAsync(string token, CancellationToken ct = default);
    /// <summary>校验 token，返回对应的用户 id；无效时返回 null。</summary>
    Task<long?> ValidateTokenAsync(string token, CancellationToken ct = default);
    /// <summary>为用户签发一个 JWT。</summary>
    string IssueToken(User user);
}

/// <summary>
/// JWT 中承载用户权限身份的自定义 claim 名。签发端（<c>JwtService</c>）与解析端
/// （<c>ClaimsPrincipal.ToUser</c>）共用同一组常量，避免两处字面量漂移 —— 漂移的表现是
/// 「token 里没有/读不到该字段」，而 <c>User.BasePath</c>/<c>Permission</c> 缺失恰好等价于不约束，
/// 是个静默失效的坑。
/// </summary>
public static class OpenListClaims
{
    /// <summary>用户文件根路径，对应 <see cref="User.BasePath"/>（用户级目录隔离）。</summary>
    public const string BasePath = "base_path";

    /// <summary>权限位掩码，对应 <see cref="User.Permission"/>。</summary>
    public const string Permission = "permission";
}
