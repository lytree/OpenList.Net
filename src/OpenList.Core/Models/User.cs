using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

public enum UserRole : int
{
    General = 0,
    Guest = 1,
    Admin = 2,
}

public sealed class User
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.General;
    public bool Disabled { get; set; }
    public int Permission { get; set; }
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }
    [JsonPropertyName("otp")]
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    [JsonPropertyName("sso_id")]
    public string? SsoID { get; set; }
    [JsonPropertyName("allow_ldap")]
    public bool AllowLdap { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UserPublic
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool Disabled { get; set; }
    public int Permission { get; set; }
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }
    [JsonPropertyName("otp")]
    public bool TwoFactorEnabled { get; set; }
    [JsonPropertyName("sso_id")]
    public string? SsoID { get; set; }
    [JsonPropertyName("allow_ldap")]
    public bool AllowLdap { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
