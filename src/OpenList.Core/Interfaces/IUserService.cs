using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IUserService
{
    Task<Resp<User>> GetAsync(long id, CancellationToken ct = default);
    Task<Resp<User>> GetByNameAsync(string username, CancellationToken ct = default);
    /// <summary>Look up a user by their SSO provider id. Used by the SSO callback flow.</summary>
    Task<Resp<User>> GetBySsoIdAsync(string ssoId, CancellationToken ct = default);
    Task<Resp<UserPublic>> GetCurrentAsync(long userId, CancellationToken ct = default);
    Task<Resp> UpdateCurrentAsync(long userId, UpdateUserReq req, CancellationToken ct = default);
    Task<Resp<List<UserPublic>>> ListAsync(CancellationToken ct = default);
    Task<Resp<UserPublic>> CreateAsync(CreateUserReq req, CancellationToken ct = default);
    Task<Resp> UpdateAsync(long id, UpdateUserReq req, CancellationToken ct = default);
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
    Task<Resp> Cancel2FaAsync(long id, CancellationToken ct = default);
    Task<Resp> SetTwoFactorSecretAsync(long id, string secret, CancellationToken ct = default);
    Task<Resp> DelCacheAsync(long id, CancellationToken ct = default);
    Task<Resp<List<SshKey>>> ListSshKeysAsync(long userId, CancellationToken ct = default);
    Task<Resp> DeleteSshKeyAsync(long keyId, CancellationToken ct = default);
}

public sealed class CreateUserReq
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.General;
    public int Permission { get; set; }
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }
    public bool Disabled { get; set; }
    [JsonPropertyName("allow_ldap")]
    public bool AllowLdap { get; set; }
}

public sealed class UpdateUserReq
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    public string? Username { get; set; }
    public string? Password { get; set; }
    public UserRole? Role { get; set; }
    public int? Permission { get; set; }
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }
    public bool? Disabled { get; set; }
    [JsonPropertyName("otp")]
    public bool? TwoFactorEnabled { get; set; }
    [JsonPropertyName("allow_ldap")]
    public bool? AllowLdap { get; set; }
    [JsonPropertyName("sso_id")]
    public string? SsoID { get; set; }
}
