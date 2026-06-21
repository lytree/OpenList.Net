using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.FileSystem;
using OpenList.Core.Configuration;

namespace OpenList.Server.FtpServer;

/// <summary>
/// Manages the FubarDev FTP server lifecycle as an IHostedService.
/// Only starts if config.FTP.Enable is true.
/// Go: server/ftp.go FtpMainDriver + main.go ftpServer.Start()
/// </summary>
public class FtpServerService : IHostedService, IAsyncDisposable
{
    private readonly AppConfiguration _config;
    private readonly ILogger<FtpServerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private IFtpServerHost? _ftpServerHost;
    private ServiceProvider? _ftpServiceProvider;

    public FtpServerService(
        AppConfiguration config,
        ILogger<FtpServerService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _config = config;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.FTP.Enable)
        {
            _logger.LogInformation("FTP server is disabled");
            return;
        }

        try
        {
            // Parse listen address (format: ":5221" or "0.0.0.0:5221")
            var (address, port) = ParseListenAddress(_config.FTP.Listen);

            // Build a separate DI container for the FTP server
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

            // Register FTP server with custom providers
            services.AddFtpServer(builder =>
            {
                // Register custom membership provider (auth against OpenList users)
                builder.Services.AddSingleton<IMembershipProvider>(sp =>
                    new OpenListFtpMembershipProvider(
                        _scopeFactory,
                        sp.GetRequiredService<ILogger<OpenListFtpMembershipProvider>>()));

                // Register custom file system factory (bridges to IFileSystemService)
                builder.Services.AddSingleton<IFileSystemClassFactory>(sp =>
                    new OpenListFtpFileSystemFactory(
                        _scopeFactory,
                        sp.GetRequiredService<ILogger<OpenListFtpFileSystem>>()));

                // Use single root (root is per-user base path, handled in file system adapter)
                builder.UseSingleRoot();
            });

            // Configure server address and port
            services.Configure<FtpServerOptions>(opt =>
            {
                opt.ServerAddress = address;
                opt.Port = port;
                if (_config.FTP.IdleTimeout > 0)
                    opt.ConnectionInactivityCheckInterval = TimeSpan.FromSeconds(_config.FTP.IdleTimeout);
            });

            // Add logging
            services.AddLogging(lb => lb.AddConsole());

            _ftpServiceProvider = services.BuildServiceProvider();
            _ftpServerHost = _ftpServiceProvider.GetRequiredService<IFtpServerHost>();

            await _ftpServerHost.StartAsync(cancellationToken);
            _logger.LogInformation("FTP server started on {Address}:{Port}", address, port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start FTP server");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ftpServerHost != null)
        {
            try
            {
                await _ftpServerHost.StopAsync(cancellationToken);
                _logger.LogInformation("FTP server stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping FTP server");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ftpServiceProvider != null)
        {
            await _ftpServiceProvider.DisposeAsync();
        }
    }

    /// <summary>
    /// Parse Go-style listen address like ":5221" or "0.0.0.0:5221" into (host, port).
    /// </summary>
    internal static (string address, int port) ParseListenAddress(string listen)
    {
        if (string.IsNullOrEmpty(listen))
            return ("", 21);

        listen = listen.Trim();

        // Handle ":port" format
        if (listen.StartsWith(':'))
        {
            if (int.TryParse(listen[1..], out var port))
                return ("", port);
        }

        // Handle "host:port" format
        var lastColon = listen.LastIndexOf(':');
        if (lastColon > 0)
        {
            var host = listen[..lastColon];
            if (int.TryParse(listen[(lastColon + 1)..], out var port))
                return (host, port);
        }

        // Handle just a port number
        if (int.TryParse(listen, out var p))
            return ("", p);

        return ("", 21);
    }
}
