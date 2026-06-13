using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IAuthService
{
    Task<string?> LoginAsync(string username, string password, string? otpCode = null, CancellationToken ct = default);
    Task<string?> LoginHashAsync(string username, string passwordHash, string? otpCode = null, CancellationToken ct = default);
    Task<string?> LoginLdapAsync(string username, string password, CancellationToken ct = default);
    Task<User?> GetCurrentUserAsync(string token, CancellationToken ct = default);
    Task<(string qr, string secret)> Generate2FAAsync(uint userId, CancellationToken ct = default);
    Task<bool> Verify2FAAsync(uint userId, string code, string secret, CancellationToken ct = default);
    Task InvalidateTokenAsync(string token, CancellationToken ct = default);
    string HashPassword(string password);
    string GenerateToken(User user);
}
