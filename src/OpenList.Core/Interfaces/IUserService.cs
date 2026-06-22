using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IUserService
{
    Task<Resp<User>> GetAsync(long id, CancellationToken ct = default);
    Task<Resp<User>> GetByNameAsync(string username, CancellationToken ct = default);
    Task<Resp<UserPublic>> GetCurrentAsync(long userId, CancellationToken ct = default);
    Task<Resp> UpdateCurrentAsync(long userId, UpdateUserReq req, CancellationToken ct = default);
    Task<Resp<List<UserPublic>>> ListAsync(CancellationToken ct = default);
    Task<Resp<UserPublic>> CreateAsync(CreateUserReq req, CancellationToken ct = default);
    Task<Resp> UpdateAsync(long id, UpdateUserReq req, CancellationToken ct = default);
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
    Task<Resp> Cancel2FaAsync(long id, CancellationToken ct = default);
    Task<Resp> DelCacheAsync(long id, CancellationToken ct = default);
    Task<Resp<List<SshKey>>> ListSshKeysAsync(long userId, CancellationToken ct = default);
    Task<Resp> DeleteSshKeyAsync(long keyId, CancellationToken ct = default);
}

public sealed class CreateUserReq
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Normal;
    public int Permission { get; set; }
    public string? BasePath { get; set; }
    public bool Disabled { get; set; }
}

public sealed class UpdateUserReq
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public UserRole? Role { get; set; }
    public int? Permission { get; set; }
    public string? BasePath { get; set; }
    public bool? Disabled { get; set; }
    public bool? TwoFactorEnabled { get; set; }
}
