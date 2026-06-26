using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// WebAuthn (passkey) registration + login backed by Fido2NetLib and the
/// <c>x_authn_credentials</c> table. Mirrors OpenList Go
/// <c>server/handles/webauthn.go</c> + <c>internal/authn</c>.
///
/// One <see cref="IFido2"/> instance is built per request because the relying
/// party's origin comes from the inbound HTTP request (the user's browser URL).
/// Sessions (challenge + user handle) are JSON blobs returned to the client and
/// echoed back in the <c>Session</c> header — same pattern as the Go server.
/// </summary>
public sealed class WebAuthnService : IWebAuthnService
{
    private readonly OpenListDbContext _db;
    private readonly IAuthService _auth;
    private readonly ISettingService _settings;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public WebAuthnService(OpenListDbContext db, IAuthService auth, ISettingService settings, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _auth = auth;
        _settings = settings;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var r = await _settings.GetAsync("webauthn_login_enabled", ct);
        return r.Code == RespCodes.Ok && string.Equals(r.Data?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Resp<WebAuthnBeginResult>> BeginLoginAsync(string? username, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            return Resp<WebAuthnBeginResult>.Fail("webauthn is not enabled", RespCodes.Forbidden);

        var fido = BuildFido2();
        List<PublicKeyCredentialDescriptor> allowed;
        if (!string.IsNullOrEmpty(username))
        {
            // Username-based login: restrict the assertion to that user's credentials.
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (user is null)
                return Resp<WebAuthnBeginResult>.Fail("user not found", RespCodes.NotFound);
            var creds = await _db.AuthnCredentials.Where(c => c.UserId == user.Id).ToListAsync(ct);
            allowed = creds.Select(ToDescriptor).ToList();
            if (allowed.Count == 0)
                return Resp<WebAuthnBeginResult>.Fail("no webauthn credentials registered for this user", RespCodes.BadRequest);
        }
        else
        {
            // Discoverable login — no allowList, the authenticator picks the credential.
            allowed = new();
        }

        var options = fido.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Preferred,
        });
        var session = SerializeSession(options);
        return Resp<WebAuthnBeginResult>.Ok(new WebAuthnBeginResult
        {
            // Frontend reads data.options.publicKey — wrap to match the
            // PublicKeyCredentialRequestOptionsJSON shape (Go protocol.CredentialAssertion).
            Options = WrapPublicKey(options),
            Session = session,
        });
    }

    public async Task<Resp<string>> FinishLoginAsync(string? username, string session, JsonElement assertionJson, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            return Resp<string>.Fail("webauthn is not enabled", RespCodes.Forbidden);
        if (string.IsNullOrEmpty(session))
            return Resp<string>.Fail("no session provided", RespCodes.BadRequest);

        var fido = BuildFido2();
        AssertionOptions options;
        try
        {
            // Session is base64(UTF8 JSON) — same encoding as the Go server
            // (base64.StdEncoding.EncodeToString(json.Marshal(sessionData)))
            // so the frontend can echo the header verbatim.
            var sessionBytes = Convert.FromBase64String(session);
            options = JsonSerializer.Deserialize<AssertionOptions>(sessionBytes)!;
        }
        catch
        {
            return Resp<string>.Fail("invalid session", RespCodes.BadRequest);
        }

        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson.GetRawText())
            ?? new AuthenticatorAssertionRawResponse();

        // Look up the stored credential by the id returned by the authenticator.
        // The response id is a base64url string — we store it the same way.
        var credentialId = response.Id ?? "";
        var stored = await _db.AuthnCredentials.FirstOrDefaultAsync(c => c.CredentialId == credentialId, ct);
        if (stored is null)
            return Resp<string>.Fail("unknown credential", RespCodes.Unauthorized);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null)
            return Resp<string>.Fail("user not found", RespCodes.NotFound);
        if (user.Disabled)
            return Resp<string>.Fail("user disabled", RespCodes.Forbidden);

        // If a username was provided, ensure it matches the credential's owner.
        if (!string.IsNullOrEmpty(username) && !string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))
            return Resp<string>.Fail("credential does not belong to this user", RespCodes.Unauthorized);

        var publicKey = Base64UrlTextEncoder.Decode(stored.PublicKey);
        VerifyAssertionResult result;
        try
        {
            result = await fido.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = publicKey,
                StoredSignatureCounter = stored.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            return Resp<string>.Fail("webauthn verification failed: " + ex.Message, RespCodes.Unauthorized);
        }

        // Bump the sign count (clone-detection counter).
        stored.SignCount = result.SignCount;
        await _db.SaveChangesAsync(ct);

        var token = _auth.IssueToken(user);
        return Resp<string>.Ok(token);
    }

    public async Task<Resp<WebAuthnBeginResult>> BeginRegistrationAsync(long userId, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            return Resp<WebAuthnBeginResult>.Fail("webauthn is not enabled", RespCodes.Forbidden);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Resp<WebAuthnBeginResult>.Fail("user not found", RespCodes.NotFound);

        var fido = BuildFido2();
        // Use the user's primary key (as a UTF-8 string) as the WebAuthn user handle.
        var userHandle = System.Text.Encoding.UTF8.GetBytes(user.Id.ToString());
        var existingCreds = await _db.AuthnCredentials.Where(c => c.UserId == user.Id).ToListAsync(ct);
        var exclude = existingCreds.Select(ToDescriptor).ToList();

        var options = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Name = user.Username,
                DisplayName = user.Username,
                Id = userHandle,
            },
            ExcludeCredentials = exclude,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        var session = SerializeSession(options);
        return Resp<WebAuthnBeginResult>.Ok(new WebAuthnBeginResult
        {
            // Frontend reads data.options.publicKey — wrap to match the
            // PublicKeyCredentialCreationOptionsJSON shape (Go protocol.CredentialCreation).
            Options = WrapPublicKey(options),
            Session = session,
        });
    }

    public async Task<Resp> FinishRegistrationAsync(long userId, string session, JsonElement attestationJson, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            return Resp.Fail("webauthn is not enabled", RespCodes.Forbidden);
        if (string.IsNullOrEmpty(session))
            return Resp.Fail("no session provided", RespCodes.BadRequest);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Resp.Fail("user not found", RespCodes.NotFound);

        var fido = BuildFido2();
        CredentialCreateOptions options;
        try
        {
            var sessionBytes = Convert.FromBase64String(session);
            options = JsonSerializer.Deserialize<CredentialCreateOptions>(sessionBytes)!;
        }
        catch
        {
            return Resp.Fail("invalid session", RespCodes.BadRequest);
        }

        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationJson.GetRawText())
            ?? new AuthenticatorAttestationRawResponse();

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (params_, _) =>
                {
                    var cid = Base64UrlTextEncoder.Encode(params_.CredentialId);
                    return !await _db.AuthnCredentials.AnyAsync(c => c.UserId == user.Id && c.CredentialId == cid, ct);
                },
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            return Resp.Fail("webauthn verification failed: " + ex.Message, RespCodes.BadRequest);
        }

        // Replace any existing credential with the same id (re-registering a key).
        var newId = Base64UrlTextEncoder.Encode(credential.Id);
        var existing = await _db.AuthnCredentials.FirstOrDefaultAsync(c => c.UserId == user.Id && c.CredentialId == newId, ct);
        if (existing is null)
        {
            existing = new WebAuthnCredential
            {
                UserId = user.Id,
                CredentialId = newId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.AuthnCredentials.Add(existing);
        }
        existing.UserHandle = System.Text.Encoding.UTF8.GetString(options.User.Id);
        existing.PublicKey = Base64UrlTextEncoder.Encode(credential.PublicKey);
        existing.SignCount = credential.SignCount;
        existing.Aaguid = credential.AaGuid.ToString();
        existing.Transports = credential.Transports is null
            ? ""
            : string.Join(",", credential.Transports.Select(t => t.ToString()));
        existing.Discoverable = true; // we required resident keys above
        await _db.SaveChangesAsync(ct);

        return Resp.Ok();
    }

    public async Task<Resp<List<WebAuthnCredential>>> ListCredentialsAsync(long userId, CancellationToken ct = default)
    {
        var list = await _db.AuthnCredentials.Where(c => c.UserId == userId).ToListAsync(ct);
        return Resp<List<WebAuthnCredential>>.Ok(list);
    }

    public async Task<Resp> DeleteCredentialAsync(long userId, string credentialId, CancellationToken ct = default)
    {
        var entity = await _db.AuthnCredentials.FirstOrDefaultAsync(c => c.UserId == userId && c.CredentialId == credentialId, ct);
        if (entity is null)
            return Resp.Fail("credential not found", RespCodes.NotFound);
        _db.AuthnCredentials.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    // ---- helpers ----

    private IFido2 BuildFido2()
    {
        var ctx = _httpContextAccessor.HttpContext;
        var req = ctx?.Request;
        var origin = req is null ? "https://localhost" : $"{req.Scheme}://{req.Host}";
        var host = new Uri(origin).Host;
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = host,
            ServerName = "OpenList",
            Origins = new HashSet<string> { origin },
        });
    }

    private static PublicKeyCredentialDescriptor ToDescriptor(WebAuthnCredential c)
    {
        var transports = c.Transports?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Enum.Parse<AuthenticatorTransport>(t, true)).ToArray() ?? [];
        return new PublicKeyCredentialDescriptor(
            PublicKeyCredentialType.PublicKey,
            Base64UrlTextEncoder.Decode(c.CredentialId),
            transports);
    }

    // Wrap the flat Fido2NetLib options object under a "publicKey" key so the
    // JSON matches the WebAuthn standard shape consumed by
    // PublicKeyCredential.parseCreationOptionsFromJSON /
    // parseRequestOptionsFromJSON on the frontend. Go's go-webauthn
    // protocol.CredentialCreation / CredentialAssertion types already nest
    // their fields under json:"publicKey", so we mirror that here.
    private static JsonElement WrapPublicKey<T>(T options) =>
        JsonSerializer.SerializeToElement(new { publicKey = options });

    // Session blob = base64(UTF8 JSON). Using standard base64 (not url-safe)
    // matches the Go server so frontend session headers are interchangeable.
    private static string SerializeSession<T>(T options) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(options));
}
