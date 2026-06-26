using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Single sign-on orchestration. Mirrors OpenList Go
/// <c>server/handles/ssologin.go</c>. Implements the OAuth2 authorization-code
/// flow for several IdPs (Casdoor, OIDC, Github, Google, Microsoft, Dingtalk).
/// The .NET port keeps Casdoor + OIDC (the most common in self-hosted setups)
/// and stubs the others — they share the same code path and only differ in
/// endpoint URLs / field names.
/// </summary>
public interface ISsoService
{
    /// <summary>True when <c>sso_login_enabled</c> is on and a client id is configured.</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// Build the IdP authorize URL to redirect the browser to.
    /// <paramref name="method"/> is <c>get_sso_id</c> (link existing account) or
    /// <c>sso_get_token</c> (log in). Mirrors Go <c>SSOLoginRedirect</c>.
    /// </summary>
    Task<Resp<string>> GetAuthorizeUrlAsync(string method, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Handle the IdP callback: exchange the auth code for an access token,
    /// fetch the user profile, and either return the SSO id (for
    /// <c>get_sso_id</c>) or issue a local JWT (for <c>sso_get_token</c>).
    /// </summary>
    /// <returns>
    /// For <c>get_sso_id</c>: <c>Data</c> is the SSO id (no token).
    /// For <c>sso_get_token</c>: <c>Data</c> is the JWT.
    /// </returns>
    Task<Resp<SsoCallbackResult>> HandleCallbackAsync(string method, string code, string? state, string redirectUri, CancellationToken ct = default);
}

/// <summary>Result of an SSO callback. Exactly one of <see cref="SsoId"/> / <see cref="Token"/> is set.</summary>
public sealed class SsoCallbackResult
{
    public string? SsoId { get; set; }
    public string? Token { get; set; }
}
