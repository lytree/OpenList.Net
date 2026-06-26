using Novell.Directory.Ldap;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// LDAP authenticator using <c>Novell.Directory.Ldap.NETStandard</c> (cross-platform).
/// Supports two bind modes:
/// <list type="bullet">
/// <item><c>BindDn</c> contains <c>%s</c> — direct bind with a templated DN (no search).</item>
/// <item><c>BindDn</c> has no placeholder — search for the user DN under <c>UserBase</c> first.</item>
/// </list>
/// The underlying Novell client is synchronous; we offload to the thread pool so the
/// ASP.NET request thread is not blocked. Mirrors OpenList Go <c>internal/op/auth.go</c>.
/// </summary>
public sealed class LdapAuthenticator : ILdapAuthenticator
{
    private readonly LdapConfig _cfg;

    public LdapAuthenticator(AppConfiguration config) => _cfg = config.Ldap ?? new();

    public bool Enabled => !string.IsNullOrWhiteSpace(_cfg.Url);

    public Task<bool> AuthenticateAsync(string username, string password, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Enabled) return false;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;

            var (host, port, ssl) = ParseUrl(_cfg.Url);
            using var conn = new LdapConnection();
            try
            {
                ct.ThrowIfCancellationRequested();
                if (ssl)
                {
                    conn.SecureSocketLayer = true;
                    if (_cfg.SkipVerify)
                    {
                        // Bypass cert errors for self-signed dev servers.
                        conn.UserDefinedServerCertValidationDelegate += (_, _, _, _) => true;
                    }
                }
                conn.Connect(host, port);

                // STARTTLS on a plain ldap:// URL is rarely combined with LDAPSSO setups;
                // users wanting TLS should switch to ldaps://. The Novell 3.6 API renames
                // the STARTTLS entry point across versions, so we skip it here for portability.

                var dn = ResolveDn(username);
                if (dn is null)
                {
                    // Search mode: bind anonymously (or as a service account) first to locate the user DN.
                    dn = SearchUserDn(conn, username);
                    if (dn is null) return false;
                }

                conn.Bind(LdapConnection.LdapV3, dn, password);
                return conn.Bound;
            }
            catch (LdapException)
            {
                // Bad credentials / connection refused — surface as auth failure, not crash.
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (conn.Connected) conn.Disconnect(); } catch { /* ignore */ }
            }
        }, ct);

    /// <summary>
    /// If <see cref="LdapConfig.BindDn"/> contains <c>%s</c>, substitute the username.
    /// Otherwise return null to signal "search mode".
    /// </summary>
    private string? ResolveDn(string username)
    {
        var tpl = _cfg.BindDn ?? "";
        if (tpl.Contains("%s")) return tpl.Replace("%s", EscapeLdapDN(username));
        return null;
    }

    private string? SearchUserDn(LdapConnection conn, string username)
    {
        // Try an anonymous bind first; if the directory forbids it the search will
        // simply throw and we return null (no leak of valid usernames).
        try { conn.Bind(LdapConnection.LdapV3, "", ""); } catch { /* anonymous may be rejected */ }
        if (!conn.Bound) return null;

        var filter = $"({_cfg.UserFilter}={EscapeLdapFilter(username)})";
        var results = conn.Search(
            _cfg.UserBase,
            LdapConnection.ScopeSub,
            filter,
            new[] { _cfg.UserFilter },
            typesOnly: false);
        // Take the first matching entry's DN.
        while (results.HasMore())
        {
            try { return results.Next().Dn; }
            catch (LdapException) { /* skip problematic entry */ }
        }
        return null;
    }

    private static (string Host, int Port, bool Ssl) ParseUrl(string url)
    {
        var u = url.Trim();
        var ssl = u.StartsWith("ldaps://", StringComparison.OrdinalIgnoreCase);
        var scheme = ssl ? "ldaps://" : "ldap://";
        if (u.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) u = u[scheme.Length..];
        // Strip any trailing path.
        var slash = u.IndexOf('/');
        if (slash >= 0) u = u[..slash];
        var colon = u.IndexOf(':');
        if (colon >= 0 && int.TryParse(u[(colon + 1)..], out var p))
            return (u[..colon], p, ssl);
        return (u, ssl ? 636 : 389, ssl);
    }

    // RFC 4514 DN escaping: escape the special characters when substituting into a DN.
    private static string EscapeLdapDN(string s)
        => s.Replace("\\", "\\\\").Replace(",", "\\,").Replace("+", "\\+")
            .Replace("\"", "\\\"").Replace("<", "\\<").Replace(">", "\\>")
            .Replace(";", "\\;").Replace("=", "\\=");

    // RFC 4515 filter escaping: parens, asterisks, backslash and NUL.
    private static string EscapeLdapFilter(string s)
        => s.Replace("\\", "\\5c").Replace("(", "\\28").Replace(")", "\\29")
            .Replace("*", "\\2a").Replace("\0", "\\00");
}
