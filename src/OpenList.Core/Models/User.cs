namespace OpenList.Core.Models;

public enum UserRole : int
{
    Guest = 0,
    Normal = 1,
    Admin = 2,
}

public sealed class User
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Normal;
    public bool Disabled { get; set; }
    public int Permission { get; set; }
    public string? BasePath { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    public bool SsoID { get; set; }
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
    public string? BasePath { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
