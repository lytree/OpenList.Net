using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.Hosting;

/// <summary>
/// Hosted service placeholder for the SFTP server.
/// Disabled by default — enable via <c>SFTP:Enable</c>.
///
/// NOTE: SSH.NET (referenced by OpenList.Drivers) is a client-only library and does not
/// provide an SSH/SFTP server implementation. A full SFTP server requires either a
/// commercial library (Rebex File Server) or a native SSH server integration.
/// This stub logs a warning when enabled so administrators know the feature is not
/// available in the current build. Mirrors OpenList Go <c>server/sftp</c> (which uses
/// a Go SSH server library).
/// </summary>
public sealed class SftpHostedService : IHostedService
{
    private readonly ILogger<SftpHostedService> _logger;
    private readonly SftpConfig _config;
    private readonly IUserService _userService;
    private readonly ZioMountService _mount;

    public SftpHostedService(
        IOptions<AppConfiguration> config,
        IUserService userService,
        ZioMountService mount,
        ILogger<SftpHostedService> logger)
    {
        _config = config.Value.SFTP;
        _userService = userService;
        _mount = mount;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enable)
        {
            _logger.LogInformation("SFTP server disabled (SFTP:Enable=false)");
            return Task.CompletedTask;
        }

        // SSH.NET is a client library — no server implementation is available in this build.
        // To enable SFTP, integrate a server-side SSH library (e.g. Rebex File Server)
        // and bridge authentication via OpenListFtpMembershipProvider-equivalent for SFTP.
        _logger.LogWarning(
            "SFTP server enabled on {Listen} but no SSH server library is wired up. " +
            "SFTP functionality is not available in this build.",
            _config.Listen);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_config.Enable)
        {
            _logger.LogInformation("SFTP server stopped");
        }
        return Task.CompletedTask;
    }
}
