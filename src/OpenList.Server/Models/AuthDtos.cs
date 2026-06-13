using System.Text.Json.Serialization;

namespace OpenList.Server.Models;

public class LoginReq { public string Username { get; set; } = ""; public string Password { get; set; } = ""; [JsonPropertyName("otp_code")] public string? OtpCode { get; set; } }
public class LoginHashReq { public string Username { get; set; } = ""; [JsonPropertyName("password")] public string PasswordHash { get; set; } = ""; [JsonPropertyName("otp_code")] public string? OtpCode { get; set; } }
public class LoginResp { [JsonPropertyName("token")] public string Token { get; set; } = ""; }
public class TwoFAResp { [JsonPropertyName("qr")] public string QR { get; set; } = ""; public string Secret { get; set; } = ""; }
public class Verify2FAReq { public string Code { get; set; } = ""; public string Secret { get; set; } = ""; }
