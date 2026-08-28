using System.Text.Json;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// WebAuthn（通行密钥）的注册与登录。与 OpenList Go 的
/// <c>server/handles/webauthn.go</c> 对应，底层基于 Fido2NetLib。
/// </summary>
public interface IWebAuthnService
{
    /// <summary>是否开启 WebAuthn 登录（配置项 <c>webauthn_login_enabled</c>）。</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// 开始通行密钥登录。若 <paramref name="username"/> 为空，则发起可发现
    /// （常驻密钥）登录，由认证器自行选择账号。
    /// 返回 FIDO2 选项以及一个不透明的会话块，客户端必须在
    /// <see cref="FinishLoginAsync"/> 的 <c>Session</c> 头中原样回传。
    /// </summary>
    Task<Resp<WebAuthnBeginResult>> BeginLoginAsync(string? username, CancellationToken ct = default);

    /// <summary>
    /// 完成通行密钥登录。校验断言与已存凭据是否匹配、更新签名计数，
    /// 并为匹配到的用户签发 JWT。
    /// </summary>
    Task<Resp<string>> FinishLoginAsync(string? username, string session, JsonElement assertionJson, CancellationToken ct = default);

    /// <summary>为已登录用户开始通行密钥注册。</summary>
    Task<Resp<WebAuthnBeginResult>> BeginRegistrationAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// 完成通行密钥注册：校验认证声明并持久化该用户的新凭据。
    /// </summary>
    Task<Resp> FinishRegistrationAsync(long userId, string session, JsonElement attestationJson, CancellationToken ct = default);

    /// <summary>列出某用户已注册的凭据（用于个人资料页）。</summary>
    Task<Resp<List<WebAuthnCredential>>> ListCredentialsAsync(long userId, CancellationToken ct = default);

    /// <summary>按其 base64url id 删除一条注册凭据。</summary>
    Task<Resp> DeleteCredentialAsync(long userId, string credentialId, CancellationToken ct = default);
}

/// <summary>BeginLogin / BeginRegistration 调用的返回结果。</summary>
public sealed class WebAuthnBeginResult
{
    /// <summary>传给 <c>navigator.credentials.create/get</c> 的选项。</summary>
    public required JsonElement Options { get; init; }
    /// <summary>不透明的会话块。客户端通过 <c>Session</c> 头原样回传。</summary>
    public required string Session { get; init; }
}
