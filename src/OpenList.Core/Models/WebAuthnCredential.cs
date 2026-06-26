using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// A registered WebAuthn credential for a user. Mirrors the Go
/// <c>model.UserAuthn</c> / <c>Credential</c> shape. Stored in the
/// <c>x_authn_credentials</c> table.
/// </summary>
public sealed class WebAuthnCredential
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>Base64url-encoded credential id (the raw id returned by the authenticator).</summary>
    public string CredentialId { get; set; } = string.Empty;

    /// <summary>Base64url-encoded user handle (the user's WebAuthn id — we use the user's primary key).</summary>
    public string UserHandle { get; set; } = string.Empty;

    /// <summary>Base64url-encoded public key (COSE format).</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Authenticator sign count (for clone detection).</summary>
    public uint SignCount { get; set; }

    /// <summary>Base64url-encoded AAGUID of the authenticator (used as a fingerprint for display).</summary>
    public string Aaguid { get; set; } = string.Empty;

    /// <summary>Transports the authenticator declared at registration time.</summary>
    public string Transports { get; set; } = string.Empty;

    /// <summary>True for discoverable credentials (resident keys).</summary>
    public bool Discoverable { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint => Aaguid;
    [JsonPropertyName("id")]
    public string DisplayId => CredentialId;
}
