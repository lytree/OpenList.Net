using OpenList.Core.Configuration;

namespace OpenList.Core.Interfaces;

/// <summary>
/// LDAP authentication abstraction for <c>/api/auth/login/ldap</c>.
/// Implementations verify a username/password against an LDAP directory and
/// optionally return extra attributes (e.g. email, display name).
/// Mirrors OpenList Go <c>internal/op/auth.go</c> LDAP login path.
/// </summary>
public interface ILdapAuthenticator
{
    /// <summary>True when the configured LDAP URL is non-empty (the feature is on).</summary>
    bool Enabled { get; }

    /// <summary>
    /// Attempt a bind with the supplied credentials. Returns true on success.
    /// Implementations should return <c>false</c> (not throw) for bad credentials.
    /// </summary>
    Task<bool> AuthenticateAsync(string username, string password, CancellationToken ct = default);
}
