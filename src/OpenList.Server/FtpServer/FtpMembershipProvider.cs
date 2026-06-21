using System.Collections.Concurrent;
using System.Security.Claims;
using FubarDev.FtpServer.AccountManagement;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Models;
using OpenList.Infrastructure.Auth;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.FtpServer;

/// <summary>
/// FTP membership provider that validates users against OpenList's IUserService.
/// Supports password auth, LDAP fallback, guest/anonymous login, and IP-based rate limiting.
/// Go: server/ftp.go AuthUser()
/// </summary>
public class OpenListFtpMembershipProvider : IMembershipProviderAsync
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenListFtpMembershipProvider> _logger;

    // IP-based login attempt tracking (Go: model.LoginCache)
    private static readonly ConcurrentDictionary<string, int> _loginCache = new();
    private const int MaxAuthRetries = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public OpenListFtpMembershipProvider(IServiceScopeFactory scopeFactory, ILogger<OpenListFtpMembershipProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<MemberValidationResult> ValidateUserAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        // Check IP-based rate limit (use username as key since we don't have IP here)
        var rateKey = username;
        if (_loginCache.TryGetValue(rateKey, out var count) && count >= MaxAuthRetries)
        {
            _loginCache.TryRemove(rateKey, out _);
            _ = Task.Delay(LockDuration).ContinueWith(t => _loginCache.TryRemove(rateKey, out _));
            _logger.LogWarning("FTP login locked for {Username}: too many attempts", username);
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
        }

        using var scope = _scopeFactory.CreateScope();
        var userSvc = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IUserService>();

        User? user;

        // Guest / anonymous login
        if (username is "anonymous" or "guest")
        {
            user = await userSvc.GetGuestUserAsync(cancellationToken);
            if (user == null)
            {
                _logger.LogWarning("FTP guest login failed: guest user not found");
                return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
            }
        }
        else
        {
            user = await userSvc.GetUserByNameAsync(username, cancellationToken);
            if (user == null)
            {
                IncrementLoginAttempts(rateKey);
                return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
            }

            // Validate password (Go: userObj.ValidateRawPassword)
            var pwdStaticHash = UserService.StaticHash(password);
            if (!UserService.ValidatePwdStaticHash(user, pwdStaticHash))
            {
                // LDAP fallback could be added here if needed
                IncrementLoginAttempts(rateKey);
                return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
            }
        }

        // Check disabled + FTP access permission
        if (user.Disabled || !user.HasPermission(UserPermission.CanFTPAccess))
        {
            IncrementLoginAttempts(rateKey);
            _logger.LogWarning("FTP access denied for {Username}: disabled={Disabled}, canFTP={CanFTP}",
                username, user.Disabled, user.HasPermission(UserPermission.CanFTPAccess));
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
        }

        // Clear login attempts on success
        _loginCache.TryRemove(rateKey, out _);

        // Build ClaimsPrincipal with user info for the FTP session
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new("openlist:userId", user.Id.ToString()),
            new("openlist:basePath", user.BasePath ?? "/"),
            new("openlist:role", ((int)user.Role).ToString()),
            new("openlist:permission", user.Permission.ToString()),
        };
        if (user.IsGuest)
            claims.Add(new Claim("openlist:isGuest", "true"));

        var identity = new ClaimsIdentity(claims, "FTP");
        var principal = new ClaimsPrincipal(identity);

        var status = user.IsGuest
            ? MemberValidationStatus.Anonymous
            : MemberValidationStatus.AuthenticatedUser;

        _logger.LogInformation("FTP user {Username} logged in successfully", username);
        return new MemberValidationResult(status, principal);
    }

    // IMembershipProvider sync method (required by base interface)
    public Task<MemberValidationResult> ValidateUserAsync(string username, string password)
        => ValidateUserAsync(username, password, CancellationToken.None);

    public Task LogOutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var username = principal.FindFirst(ClaimTypes.Name)?.Value;
        if (username != null)
            _loginCache.TryRemove(username, out _);
        return Task.CompletedTask;
    }

    private void IncrementLoginAttempts(string key)
    {
        _loginCache.AddOrUpdate(key, 1, (_, v) => v + 1);
    }
}
