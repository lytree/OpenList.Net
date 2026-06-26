using System.Text.Json.Serialization;

namespace OpenList.Server.Models;

/// <summary>Login request body for <c>POST /api/auth/login</c>.</summary>
public sealed class LoginReq
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? OtpCode { get; set; }
}

/// <summary>2FA verification body for <c>POST /api/auth/login/2fa</c>.</summary>
public sealed class Login2FaReq
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string OtpCode { get; set; } = string.Empty;
}

public sealed class Generate2FaResp
{
    public string Secret { get; set; } = string.Empty;
    public string QrUri { get; set; } = string.Empty;
}

public sealed class Verify2FaReq
{
    public string Code { get; set; } = string.Empty;
}

public sealed class UpdateMeReq
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    [JsonPropertyName("sso_id")]
    public string? SsoId { get; set; }
}

public sealed class HashPasswordReq
{
    public string Password { get; set; } = string.Empty;
}

public sealed class HashPasswordResp
{
    public string Hash { get; set; } = string.Empty;
}
