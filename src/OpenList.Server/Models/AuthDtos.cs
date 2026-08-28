using System.Text.Json.Serialization;

namespace OpenList.Server.Models;

/// <summary>Login request body for <c>POST /api/auth/login</c>.</summary>
public sealed class LoginReq
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? OtpCode { get; set; }
}

/// <summary>2FA（双因素认证）验证请求体，用于 <c>POST /api/auth/login/2fa</c>。</summary>
public sealed class Login2FaReq
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string OtpCode { get; set; } = string.Empty;
}

/// <summary>生成 2FA 密钥的响应 DTO（ASCII 密钥 + OTP 认证 URI）。</summary>
public sealed class Generate2FaResp
{
    public string Secret { get; set; } = string.Empty;
    public string QrUri { get; set; } = string.Empty;
}

/// <summary>2FA 校验请求体，携带验证码。</summary>
public sealed class Verify2FaReq
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>更新当前用户资料请求体（用户名/密码/SsoId 均可选）。</summary>
public sealed class UpdateMeReq
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    [JsonPropertyName("sso_id")]
    public string? SsoId { get; set; }
}

/// <summary>密码哈希计算请求体。</summary>
public sealed class HashPasswordReq
{
    public string Password { get; set; } = string.Empty;
}

/// <summary>密码哈希计算结果响应 DTO。</summary>
public sealed class HashPasswordResp
{
    public string Hash { get; set; } = string.Empty;
}
