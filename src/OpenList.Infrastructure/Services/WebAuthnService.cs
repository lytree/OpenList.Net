using System.Security.Cryptography;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class WebAuthnService
{
    private readonly OpenListDbContext _db;
    private readonly IFido2 _fido2;
    private readonly IMemoryCache _cache;

    public WebAuthnService(OpenListDbContext db, IFido2 fido2, IMemoryCache cache)
    {
        _db = db;
        _fido2 = fido2;
        _cache = cache;
    }

    /// <summary>
    /// Begin registration: returns options for the browser to create a credential
    /// </summary>
    public async Task<CredentialCreateOptions> BeginRegistrationAsync(uint userId, string username, string? credentialName = null, CancellationToken ct = default)
    {
        var existingCredentials = await _db.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .ToListAsync(ct);

        var excludeCredentials = existingCredentials.Select(c =>
            new PublicKeyCredentialDescriptor(DecodeBase64Url(c.CredentialId))).ToList();

        var userHandle = RandomNumberGenerator.GetBytes(32);

        var fidoUser = new Fido2User
        {
            Id = userHandle,
            Name = username,
            DisplayName = username
        };

        var options = _fido2.RequestNewCredential(
            new RequestNewCredentialParams
            {
                User = fidoUser,
                ExcludeCredentials = excludeCredentials,
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    AuthenticatorAttachment = AuthenticatorAttachment.CrossPlatform,
                    ResidentKey = ResidentKeyRequirement.Preferred,
                    UserVerification = UserVerificationRequirement.Preferred
                },
                AttestationPreference = AttestationConveyancePreference.None
            });

        return options;
    }

    /// <summary>
    /// Complete registration: verify the credential from the browser and store it
    /// </summary>
    public async Task<WebAuthnCredential> FinishRegistrationAsync(uint userId, string credentialName, AuthenticatorAttestationRawResponse attestationResponse, CancellationToken ct = default)
    {
        var options = GetCachedRegistrationOptions(userId)
            ?? throw new InvalidOperationException("Registration options not found or expired. Please start registration again.");

        IsCredentialIdUniqueToUserAsyncDelegate callback = async (args, token) =>
        {
            return !await _db.WebAuthnCredentials
                .AnyAsync(c => c.CredentialId == EncodeBase64Url(args.CredentialId), token);
        };

        var credential = await _fido2.MakeNewCredentialAsync(
            new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = callback
            }, cancellationToken: ct);

        var stored = new WebAuthnCredential
        {
            UserId = userId,
            CredentialId = EncodeBase64Url(credential.Id),
            PublicKey = Convert.ToBase64String(credential.PublicKey),
            UserHandle = EncodeBase64Url(credential.User.Id),
            SignCount = (int)credential.SignCount,
            Transports = SerializeTransports(credential.Transports),
            AaGuid = credential.AaGuid.ToString(),
            Name = credentialName ?? $"Key {DateTime.UtcNow:yyyyMMddHHmmss}",
            RegisteredAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow
        };

        _db.WebAuthnCredentials.Add(stored);
        await _db.SaveChangesAsync(ct);
        return stored;
    }

    /// <summary>
    /// Begin login: returns options for the browser to authenticate
    /// </summary>
    public async Task<AssertionOptions> BeginLoginAsync(string? username, CancellationToken ct = default)
    {
        List<PublicKeyCredentialDescriptor> allowedCredentials = [];

        if (!string.IsNullOrEmpty(username))
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (user != null)
            {
                var creds = await _db.WebAuthnCredentials
                    .Where(c => c.UserId == user.Id)
                    .ToListAsync(ct);
                allowedCredentials = creds.Select(c =>
                    new PublicKeyCredentialDescriptor(DecodeBase64Url(c.CredentialId))).ToList();
            }
        }

        var options = _fido2.GetAssertionOptions(
            new GetAssertionOptionsParams
            {
                AllowedCredentials = allowedCredentials,
                UserVerification = UserVerificationRequirement.Preferred
            });

        return options;
    }

    /// <summary>
    /// Complete login: verify the assertion and return the user ID
    /// </summary>
    public async Task<uint?> FinishLoginAsync(string username, AuthenticatorAssertionRawResponse assertionResponse, CancellationToken ct = default)
    {
        var credentialId = EncodeBase64Url(assertionResponse.RawId);
        var storedCredential = await _db.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credentialId, ct);

        if (storedCredential == null) return null;

        var options = GetCachedAssertionOptions(username);
        if (options == null) return null;

        var storedKey = Convert.FromBase64String(storedCredential.PublicKey);

        IsUserHandleOwnerOfCredentialIdAsync callback = async (args, token) =>
        {
            return await _db.WebAuthnCredentials
                .AnyAsync(c => c.CredentialId == EncodeBase64Url(args.CredentialId)
                            && c.UserHandle == EncodeBase64Url(args.UserHandle), token);
        };

        var result = await _fido2.MakeAssertionAsync(
            new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = options,
                StoredPublicKey = storedKey,
                StoredSignatureCounter = (uint)storedCredential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = callback
            }, cancellationToken: ct);

        // Update sign count
        storedCredential.SignCount = (int)result.SignCount;
        storedCredential.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return storedCredential.UserId;
    }

    /// <summary>
    /// Get all credentials for a user
    /// </summary>
    public async Task<List<WebAuthnCredential>> GetCredentialsAsync(uint userId, CancellationToken ct = default)
        => await _db.WebAuthnCredentials.Where(c => c.UserId == userId).ToListAsync(ct);

    /// <summary>
    /// Delete a credential
    /// </summary>
    public async Task DeleteCredentialAsync(int credentialId, uint userId, CancellationToken ct = default)
    {
        var cred = await _db.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, ct);
        if (cred != null)
        {
            _db.WebAuthnCredentials.Remove(cred);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Cache registration options for later retrieval during finish
    /// </summary>
    public void CacheRegistrationOptions(uint userId, CredentialCreateOptions options)
        => _cache.Set($"webauthn_reg_{userId}", options, TimeSpan.FromMinutes(5));

    /// <summary>
    /// Cache assertion options for later retrieval during finish
    /// </summary>
    public void CacheAssertionOptions(string username, AssertionOptions options)
        => _cache.Set($"webauthn_login_{username}", options, TimeSpan.FromMinutes(5));

    private CredentialCreateOptions? GetCachedRegistrationOptions(uint userId)
        => _cache.Get<CredentialCreateOptions>($"webauthn_reg_{userId}");

    private AssertionOptions? GetCachedAssertionOptions(string username)
        => _cache.Get<AssertionOptions>($"webauthn_login_{username}");

    private static string EncodeBase64Url(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static byte[] DecodeBase64Url(string encoded)
    {
        var s = encoded.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    private static string SerializeTransports(AuthenticatorTransport[]? transports)
    {
        if (transports == null || transports.Length == 0) return "[]";
        return System.Text.Json.JsonSerializer.Serialize(transports.Select(t => t.ToString()).ToList());
    }
}
