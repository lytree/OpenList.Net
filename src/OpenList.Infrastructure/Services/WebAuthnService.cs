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
/// WebAuthn（通行密钥）注册与登录服务，基于 Fido2NetLib 与 <c>x_authn_credentials</c> 数据表。
/// 对应 Go 参考实现 <c>server/handles/webauthn.go</c> + <c>internal/authn</c>。
/// <para>
/// 每次请求都会构建一个 <see cref="IFido2"/> 实例，因为依赖方（relying party）的
/// origin 来自入站 HTTP 请求（用户浏览器的 URL）。会话（challenge + user handle）以
/// JSON 形式返回给客户端，并在后续请求中通过 <c>Session</c> 头原样回传 —— 与 Go 服务一致。
/// </para>
/// </summary>
public sealed class WebAuthnService : IWebAuthnService
{
    private readonly OpenListDbContext _db;
    private readonly IAuthService _auth;
    private readonly ISettingService _settings;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// 创建 WebAuthn 服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="auth">认证服务，用于签发 JWT。</param>
    /// <param name="settings">设置服务，用于读取 WebAuthn 开关。</param>
    /// <param name="httpContextAccessor">HTTP 上下文访问器，用于解析当前请求的 origin。</param>
    public WebAuthnService(OpenListDbContext db, IAuthService auth, ISettingService settings, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _auth = auth;
        _settings = settings;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// 判断 WebAuthn 是否启用（读取 <c>webauthn_login_enabled</c> 设置）。
    /// </summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var r = await _settings.GetAsync("webauthn_login_enabled", ct);
        return r.Code == RespCodes.Ok && string.Equals(r.Data?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 开始登录流程：生成断言选项（assertion options），可指定用户名限制只允许该用户的凭证。
    /// </summary>
    /// <param name="username">用户名（可选）。提供时只能使用该用户已注册的凭证登录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>包含断言选项与会话数据的 <c>WebAuthnBeginResult</c>；未启用或用户不存在时返回错误。</returns>
    public async Task<Resp<WebAuthnBeginResult>> BeginLoginAsync(string? username, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            return Resp<WebAuthnBeginResult>.Fail("webauthn is not enabled", RespCodes.Forbidden);

        var fido = BuildFido2();
        List<PublicKeyCredentialDescriptor> allowed;
        if (!string.IsNullOrEmpty(username))
        {
            // 基于用户名的登录：把断言限定到该用户的凭证上。
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
            // 可发现式登录（discoverable login）—— 不提供 allowList，由认证器自行选择凭证。
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
            // 前端读取 data.options.publicKey —— 包装以匹配
            // PublicKeyCredentialRequestOptionsJSON 的形状（Go 的 protocol.CredentialAssertion）。
            Options = WrapPublicKey(options),
            Session = session,
        });
    }

    /// <summary>
    /// 完成登录流程：校验断言响应，查找对应凭证，验证签名并递增签名计数器，最后签发 JWT。
    /// </summary>
    /// <param name="username">期望的用户名（可选，用于校验凭证归属）。</param>
    /// <param name="session">登录开始时返回的会话数据（base64 编码）。</param>
    /// <param name="assertionJson">认证器返回的断言响应 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>登录成功时返回签发的 JWT；验证失败返回对应错误。</returns>
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
            // 会话为 base64(UTF8 JSON) —— 与 Go 服务编码一致
            // （base64.StdEncoding.EncodeToString(json.Marshal(sessionData))），
            // 这样前端可以原样回传该头。
            var sessionBytes = Convert.FromBase64String(session);
            options = JsonSerializer.Deserialize<AssertionOptions>(sessionBytes)!;
        }
        catch
        {
            return Resp<string>.Fail("invalid session", RespCodes.BadRequest);
        }

        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson.GetRawText())
            ?? new AuthenticatorAssertionRawResponse();

        // 按认证器返回的 id 查找已存凭证。该 id 是 base64url 字符串，存库时也采用相同编码。
        var credentialId = response.Id ?? "";
        var stored = await _db.AuthnCredentials.FirstOrDefaultAsync(c => c.CredentialId == credentialId, ct);
        if (stored is null)
            return Resp<string>.Fail("unknown credential", RespCodes.Unauthorized);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null)
            return Resp<string>.Fail("user not found", RespCodes.NotFound);
        if (user.Disabled)
            return Resp<string>.Fail("user disabled", RespCodes.Forbidden);

        // 若提供了用户名，确保其与该凭证的主人一致。
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

        // 更新签名计数器（用于克隆检测）。
        stored.SignCount = result.SignCount;
        await _db.SaveChangesAsync(ct);

        var token = _auth.IssueToken(user);
        return Resp<string>.Ok(token);
    }

    /// <summary>
    /// 开始注册流程：为指定用户生成新凭证（create credential）选项，绑定为常驻密钥。
    /// </summary>
    /// <param name="userId">要注册凭证的用户主键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>包含创建选项与会话数据的 <c>WebAuthnBeginResult</c>。</returns>
    public async Task<Resp<WebAuthnBeginResult>> BeginRegistrationAsync(long userId, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            return Resp<WebAuthnBeginResult>.Fail("webauthn is not enabled", RespCodes.Forbidden);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Resp<WebAuthnBeginResult>.Fail("user not found", RespCodes.NotFound);

        var fido = BuildFido2();
        // 用用户主键（转 UTF-8 字符串）作为 WebAuthn 用户句柄。
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
            // 前端读取 data.options.publicKey —— 包装以匹配
            // PublicKeyCredentialCreationOptionsJSON 的形状（Go 的 protocol.CredentialCreation）。
            Options = WrapPublicKey(options),
            Session = session,
        });
    }

    /// <summary>
    /// 完成注册流程：校验认证器响应，创建或更新凭证记录。
    /// </summary>
    /// <param name="userId">注册凭证的用户主键。</param>
    /// <param name="session">注册开始时返回的会话数据。</param>
    /// <param name="attestationJson">认证器返回的注册响应 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>注册成功返回空成功响应；验证失败返回对应错误。</returns>
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

        // 用同 id 覆盖既有凭证（重新注册同一把密钥）。
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
        existing.Discoverable = true; // 上面已强制要求常驻密钥
        await _db.SaveChangesAsync(ct);

        return Resp.Ok();
    }

    /// <summary>
    /// 列出指定用户已注册的所有 WebAuthn 凭证。
    /// </summary>
    public async Task<Resp<List<WebAuthnCredential>>> ListCredentialsAsync(long userId, CancellationToken ct = default)
    {
        var list = await _db.AuthnCredentials.Where(c => c.UserId == userId).ToListAsync(ct);
        return Resp<List<WebAuthnCredential>>.Ok(list);
    }

    /// <summary>
    /// 删除指定用户的某条 WebAuthn 凭证。
    /// </summary>
    public async Task<Resp> DeleteCredentialAsync(long userId, string credentialId, CancellationToken ct = default)
    {
        var entity = await _db.AuthnCredentials.FirstOrDefaultAsync(c => c.UserId == userId && c.CredentialId == credentialId, ct);
        if (entity is null)
            return Resp.Fail("credential not found", RespCodes.NotFound);
        _db.AuthnCredentials.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    // ---- 辅助方法 ----

    /// <summary>
    /// 依据当前请求的 scheme、host 构建 Fido2 实例（每次请求新建，因 origin 来自请求）。
    /// </summary>
    private IFido2 BuildFido2()
    {
        var ctx = _httpContextAccessor.HttpContext;
        var req = ctx?.Request;
        var origin = req is null ? "https://localhost" : $"{req.Scheme}://{req.Host}";
        var host = new Uri(origin).Host;
        return new Fido2(new Fido2Configuration
        {
            RPID = host,
            RPName = "OpenList",
            Origins = new HashSet<string> { origin },
        });
    }

    /// <summary>
    /// 把数据库中的凭证记录转换为 Fido2NetLib 使用的描述符（含传输方式解析）。
    /// </summary>
    private static PublicKeyCredentialDescriptor ToDescriptor(WebAuthnCredential c)
    {
        var transports = c.Transports?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Enum.Parse<AuthenticatorTransport>(t, true)).ToArray() ?? [];
        return new PublicKeyCredentialDescriptor(
            PublicKeyCredentialType.PublicKey,
            Base64UrlTextEncoder.Decode(c.CredentialId),
            transports);
    }

    // 把扁平的 Fido2NetLib options 对象包到 "publicKey" 键下，使 JSON 符合前端
    // PublicKeyCredential.parseCreationOptionsFromJSON /
    // parseRequestOptionsFromJSON 使用的 WebAuthn 标准形状。Go 的 go-webauthn
    // protocol.CredentialCreation / CredentialAssertion 类型已把字段嵌套在
    // json:"publicKey" 下，这里保持一致。
    private static JsonElement WrapPublicKey<T>(T options) =>
        JsonSerializer.SerializeToElement(new { publicKey = options });

    // 会话 = base64(UTF8 JSON)。使用标准 base64（非 url 安全）以与 Go 服务一致，
    // 保证前端会话头可互通。
    private static string SerializeSession<T>(T options) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(options));
}