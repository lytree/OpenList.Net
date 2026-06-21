using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.SftpServer;

/// <summary>
/// Handles SFTP authentication: password + SSH public key + guest NoClientAuth.
/// Go: server/sftp.go PasswordAuth(), PublicKeyAuth(), NoClientAuth()
/// </summary>
public class SftpAuthHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SftpAuthHandler> _logger;

    // IP-based login attempt tracking
    private static readonly ConcurrentDictionary<string, int> _loginCache = new();
    private const int MaxAuthRetries = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public SftpAuthHandler(IServiceScopeFactory scopeFactory, ILogger<SftpAuthHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Password authentication. Returns (User, error message).
    /// Go: server/sftp.go PasswordAuth()
    /// </summary>
    public async Task<(User? user, string? error)> PasswordAuthAsync(
        string username, string password, string remoteIp, CancellationToken ct = default)
    {
        // Check rate limit
        if (_loginCache.TryGetValue(remoteIp, out var count) && count >= MaxAuthRetries)
        {
            _loginCache.TryRemove(remoteIp, out _);
            _ = Task.Delay(LockDuration).ContinueWith(t => _loginCache.TryRemove(remoteIp, out _));
            return (null, "Too many unsuccessful sign-in attempts. Try again later.");
        }

        using var scope = _scopeFactory.CreateScope();
        var userSvc = scope.ServiceProvider.GetRequiredService<IUserService>();

        var user = await userSvc.GetUserByNameAsync(username, ct);
        if (user == null)
        {
            IncrementAttempts(remoteIp);
            return (null, "Invalid username or password");
        }

        // Validate password
        var pwdHash = UserService.StaticHash(password);
        if (!UserService.ValidatePwdStaticHash(user, pwdHash))
        {
            IncrementAttempts(remoteIp);
            return (null, "Invalid username or password");
        }

        // Check disabled + SFTP/FTP access permission (Go uses CanFTPAccess for SFTP too)
        if (user.Disabled || !user.HasPermission(UserPermission.CanFTPAccess))
        {
            IncrementAttempts(remoteIp);
            return (null, "User is not allowed to access via SFTP");
        }

        _loginCache.TryRemove(remoteIp, out _);
        _logger.LogInformation("[SFTP] {Username}({IP}) logged in via password", username, remoteIp);
        return (user, null);
    }

    /// <summary>
    /// Guest NoClientAuth. Only guest user is allowed without authentication.
    /// Go: server/sftp.go NoClientAuth()
    /// </summary>
    public async Task<(User? user, string? error)> NoClientAuthAsync(
        string username, CancellationToken ct = default)
    {
        if (username != "guest")
            return (null, "Only guest is allowed to login without authorization");

        using var scope = _scopeFactory.CreateScope();
        var userSvc = scope.ServiceProvider.GetRequiredService<IUserService>();
        var guest = await userSvc.GetGuestUserAsync(ct);
        if (guest == null)
            return (null, "Guest user not found");

        if (guest.Disabled || !guest.HasPermission(UserPermission.CanFTPAccess))
            return (null, "Guest user is not allowed to access via SFTP");

        _logger.LogInformation("[SFTP] guest logged in via NoClientAuth");
        return (guest, null);
    }

    /// <summary>
    /// SSH public key authentication. Matches user's stored SSH keys.
    /// Go: server/sftp.go PublicKeyAuth()
    /// </summary>
    public async Task<(User? user, string? error)> PublicKeyAuthAsync(
        string username, byte[] publicKeyBlob, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var userSvc = scope.ServiceProvider.GetRequiredService<IUserService>();

        var user = await userSvc.GetUserByNameAsync(username, ct);
        if (user == null)
            return (null, "User not found");

        if (user.Disabled || !user.HasPermission(UserPermission.CanFTPAccess))
            return (null, "User is not allowed to access via SFTP");

        // Get user's SSH keys
        var (keys, _) = await userSvc.ListSshKeysPagedAsync(user.Id, 1, -1, ct);
        if (keys == null || keys.Count == 0)
            return (null, "No SSH keys registered for this user");

        // Compare public key blob with stored keys
        var keyBlobBase64 = Convert.ToBase64String(publicKeyBlob);
        foreach (var sk in keys)
        {
            if (MatchesPublicKey(sk.PublicKey, keyBlobBase64, publicKeyBlob))
            {
                _logger.LogInformation("[SFTP] {Username} logged in via public key", username);
                return (user, null);
            }
        }

        return (null, "Public key refused");
    }

    /// <summary>
    /// Match stored SSH key against presented public key blob.
    /// The stored key is in OpenSSH authorized_keys format: "ssh-rsa AAAA... comment"
    /// </summary>
    private static bool MatchesPublicKey(string storedKey, string presentedBase64, byte[] presentedBlob)
    {
        if (string.IsNullOrEmpty(storedKey)) return false;

        // Direct base64 comparison (stored key format: "type base64 comment")
        var parts = storedKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return parts[1] == presentedBase64;
        }

        return storedKey == presentedBase64;
    }

    private void IncrementAttempts(string key)
    {
        _loginCache.AddOrUpdate(key, 1, (_, v) => v + 1);
    }
}
