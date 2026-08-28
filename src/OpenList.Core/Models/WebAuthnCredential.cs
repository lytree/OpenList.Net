using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 用户已注册的 WebAuthn 凭据。与 Go <c>model.UserAuthn</c> / <c>Credential</c> 结构对应，
/// 存储在 <c>x_authn_credentials</c> 表中。
/// </summary>
public sealed class WebAuthnCredential
{
    /// <summary>凭据主键。</summary>
    public long Id { get; set; }
    /// <summary>所属用户主键。</summary>
    public long UserId { get; set; }

    /// <summary>Base64url 编码的凭据 id（认证器返回的原始 id）。</summary>
    public string CredentialId { get; set; } = string.Empty;

    /// <summary>Base64url 编码的用户句柄（用户的 WebAuthn id——此处使用用户主键）。</summary>
    public string UserHandle { get; set; } = string.Empty;

    /// <summary>Base64url 编码的公钥（COSE 格式）。</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>认证器签名计数（用于克隆检测）。</summary>
    public uint SignCount { get; set; }

    /// <summary>认证器的 Base64url 编码 AAGUID（作为展示用指纹）。</summary>
    public string Aaguid { get; set; } = string.Empty;

    /// <summary>认证器在注册时声明的传输方式。</summary>
    public string Transports { get; set; } = string.Empty;

    /// <summary>是否为可发现凭据（常驻密钥）。</summary>
    public bool Discoverable { get; set; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>对外展示的指纹（即 AAGUID）。</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint => Aaguid;
    /// <summary>对外展示的凭据 id（即 CredentialId）。</summary>
    [JsonPropertyName("id")]
    public string DisplayId => CredentialId;
}
