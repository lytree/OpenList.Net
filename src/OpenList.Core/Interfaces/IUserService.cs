using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IUserService
{
    Task<User?> GetUserByNameAsync(string username, CancellationToken ct = default);
    Task<User?> GetUserByIdAsync(uint id, CancellationToken ct = default);
    Task<(List<User> Users, int Total)> ListUsersAsync(int page, int perPage, CancellationToken ct = default);
    Task<User> CreateUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, CancellationToken ct = default);
    Task DeleteUserAsync(uint id, CancellationToken ct = default);
    Task SetPasswordAsync(User user, string password, CancellationToken ct = default);
    Task Cancel2FAAsync(uint id, CancellationToken ct = default);
    Task DeleteUserCacheAsync(uint id, CancellationToken ct = default);
    Task DeleteUserCacheByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> GetUserBySsoIdAsync(string ssoId, CancellationToken ct = default);
    Task<User?> GetGuestUserAsync(CancellationToken ct = default);
    Task<List<SshKey>> ListSshKeysAsync(uint userId, CancellationToken ct = default);
    Task<(List<SshKey> Keys, int Total)> ListSshKeysPagedAsync(uint userId, int page, int perPage, CancellationToken ct = default);
    Task AddSshKeyAsync(uint userId, string name, string key, CancellationToken ct = default);
    Task DeleteSshKeyAsync(uint userId, string name, CancellationToken ct = default);
    Task DeleteSshKeyByIdAsync(uint id, CancellationToken ct = default);
}
