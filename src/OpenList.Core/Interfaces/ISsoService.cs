using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 单点登录（SSO）编排。与 OpenList Go 的
/// <c>server/handles/ssologin.go</c> 对应。实现了针对多个身份提供方
/// （Casdoor、OIDC、Github、Google、Microsoft、Dingtalk）的 OAuth2 授权码流程。
/// .NET 移植版本保留了 Casdoor 和 OIDC（自托管部署中最常见），其余作为桩实现——
/// 它们共享相同的代码路径，仅凭端点 URL / 字段名不同。
/// </summary>
public interface ISsoService
{
    /// <summary>是否开启 SSO 登录（配置项 <c>sso_login_enabled</c>）且已配置客户端 id。</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// 构建用于将浏览器重定向到 IdP 的授权 URL。
    /// <paramref name="method"/> 为 <c>get_sso_id</c>（绑定已有账号）或
    /// <c>sso_get_token</c>（登录）。与 Go 的 <c>SSOLoginRedirect</c> 对应。
    /// </summary>
    Task<Resp<string>> GetAuthorizeUrlAsync(string method, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// 处理 IdP 回调：用授权码换取访问令牌、拉取用户资料，随后
    /// 对于 <c>get_sso_id</c> 返回 SSO id，对于 <c>sso_get_token</c> 签发本地 JWT。
    /// </summary>
    /// <returns>
    /// 对于 <c>get_sso_id</c>：<c>Data</c> 为 SSO id（无 token）。
    /// 对于 <c>sso_get_token</c>：<c>Data</c> 为 JWT。
    /// </returns>
    Task<Resp<SsoCallbackResult>> HandleCallbackAsync(string method, string code, string? state, string redirectUri, CancellationToken ct = default);
}

/// <summary>SSO 回调结果。<see cref="SsoId"/> 与 <see cref="Token"/> 恰好有一个被设置。</summary>
public sealed class SsoCallbackResult
{
    /// <summary>SSO 身份标识。</summary>
    public string? SsoId { get; set; }
    /// <summary>签发的 JWT。</summary>
    public string? Token { get; set; }
}
