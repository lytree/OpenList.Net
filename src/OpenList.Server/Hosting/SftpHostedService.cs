using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;

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

    public SftpHostedService(IOptions<AppConfiguration> config, ILogger<SftpHostedService> logger)
    {
        _config = config.Value.SFTP;
        _logger = logger;
    }

    /// <summary>启动 SFTP 服务；未启用时仅记录日志，启用时由于缺少服务端库仅给出警告。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enable)
        {
            _logger.LogInformation("SFTP server disabled (SFTP:Enable=false)");
            return Task.CompletedTask;
        }

        // SSH.NET 是客户端库 —— 本构建无可用服务端实现。
        // 如需启用 SFTP，接入服务端 SSH 库（如 Rebex File Server），
        // 并将鉴权桥接到与 OpenListFtpMembershipProvider 等价的 SFTP 实现。
        _logger.LogWarning(
            "SFTP server enabled on {Listen} but no SSH server library is wired up. " +
            "SFTP functionality is not available in this build.",
            _config.Listen);

        return Task.CompletedTask;
    }

    /// <summary>停止 SFTP 服务（仅当启用时记录日志）。</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_config.Enable)
        {
            _logger.LogInformation("SFTP server stopped");
        }
        return Task.CompletedTask;
    }
}
