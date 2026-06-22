using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Authentication service: login (password/hash/ldap/sso), JWT issue/verify, 2FA, logout.
/// Mirrors OpenList Go <c>server/handles/auth.go</c>.
/// </summary>
public interface IAuthService
{
    Task<Resp<string>> LoginAsync(string username, string password, string? otpCode = null, CancellationToken ct = default);
    Task<Resp<string>> LoginHashAsync(string username, string passwordHash, string? otpCode = null, CancellationToken ct = default);
    Task<Resp<string>> LoginLdapAsync(string username, string password, CancellationToken ct = default);
    Task<Resp<string>> Generate2FaAsync(long userId, CancellationToken ct = default);
    Task<Resp> Verify2FaAsync(long userId, string code, CancellationToken ct = default);
    Task<Resp> LogoutAsync(string token, CancellationToken ct = default);
    Task<long?> ValidateTokenAsync(string token, CancellationToken ct = default);
    string IssueToken(User user);
}
