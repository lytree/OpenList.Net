using System.Text.Json;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// WebAuthn (passkey) registration + login. Mirrors OpenList Go
/// <c>server/handles/webauthn.go</c> using Fido2NetLib under the hood.
/// </summary>
public interface IWebAuthnService
{
    /// <summary>True when <c>webauthn_login_enabled</c> is on.</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// Begin passkey login. If <paramref name="username"/> is empty, starts a
    /// discoverable (resident-key) login so the authenticator picks the account.
    /// Returns the FIDO2 options + an opaque session blob the client must echo
    /// back in the <c>Session</c> header of <see cref="FinishLoginAsync"/>.
    /// </summary>
    Task<Resp<WebAuthnBeginResult>> BeginLoginAsync(string? username, CancellationToken ct = default);

    /// <summary>
    /// Finish passkey login. Validates the assertion against the stored credential,
    /// bumps the sign count, and issues a JWT for the matched user.
    /// </summary>
    Task<Resp<string>> FinishLoginAsync(string? username, string session, JsonElement assertionJson, CancellationToken ct = default);

    /// <summary>Begin passkey registration for the signed-in user.</summary>
    Task<Resp<WebAuthnBeginResult>> BeginRegistrationAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Finish passkey registration: validate the attestation and persist the
    /// new credential for the user.
    /// </summary>
    Task<Resp> FinishRegistrationAsync(long userId, string session, JsonElement attestationJson, CancellationToken ct = default);

    /// <summary>List the registered credentials for a user (for the Profile page).</summary>
    Task<Resp<List<WebAuthnCredential>>> ListCredentialsAsync(long userId, CancellationToken ct = default);

    /// <summary>Delete a registered credential by its base64url id.</summary>
    Task<Resp> DeleteCredentialAsync(long userId, string credentialId, CancellationToken ct = default);
}

/// <summary>Result of a BeginLogin / BeginRegistration call.</summary>
public sealed class WebAuthnBeginResult
{
    /// <summary>The options to pass to <c>navigator.credentials.create/get</c>.</summary>
    public required JsonElement Options { get; init; }
    /// <summary>Opaque session blob. The client sends this back in the <c>Session</c> header.</summary>
    public required string Session { get; init; }
}
