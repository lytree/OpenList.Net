using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using JustSFTP.Server;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.Hosting;

/// <summary>
/// 托管服务，运行一个以 <see cref="MountFileSystem"/> 为后端的 SFTP 服务器。
/// 默认关闭 —— 通过 <c>SFTP:Enable</c> 开启，监听地址来自 <c>SFTP:Listen</c>（默认 <c>:5222</c>）。
/// <para>
/// SSH 传输与密码认证由 Microsoft.DevTunnels.Ssh 提供，SFTP v3 协议由 JustSFTP.Server 提供；
/// 每个会话以登录用户的 BasePath 为根，鉴权规则与 FTP / WebDAV / S3 一致。
/// </para>
/// <para>
/// 主机密钥持久化在数据库同目录的 <c>sftp_host_key</c>（PKCS#8 PEM，Unix 下权限 600）：
/// 首次启动生成 ECDSA P-256（失败回退 RSA-2048）并写盘，之后每次启动复用同一个密钥，
/// 客户端已信任的指纹在重启后依然有效。删除该文件即可强制轮换（客户端会重新提示未知主机密钥）。
/// </para>
/// </summary>
public sealed class SftpHostedService : IHostedService
{
    /// <summary>主机密钥文件名（与 <c>jwt_secret</c> 同级，位于数据库所在目录）。</summary>
    private const string HostKeyFileName = "sftp_host_key";

    private readonly IServiceScopeFactory _scopes;
    private readonly MountService _mount;
    private readonly SftpConfig _config;
    private readonly ILogger<SftpHostedService> _logger;
    private readonly string _hostKeyPath;

    private readonly ConcurrentDictionary<long, SshServerSession> _sessions = new();
    private readonly TraceSource _trace = new("OpenList.Sftp", SourceLevels.Warning);
    private long _sessionSequence;

    private SshServerCredentials? _credentials;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public SftpHostedService(
        IServiceScopeFactory scopes,
        MountService mount,
        IOptions<AppConfiguration> config,
        ILogger<SftpHostedService> logger)
    {
        _scopes = scopes;
        _mount = mount;
        _config = config.Value.SFTP;
        _logger = logger;

        // 与 JWT 密钥同样的位置约定：数据库文件所在目录。
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(config.Value.Database.DBFile))
                      ?? AppContext.BaseDirectory;
        _hostKeyPath = Path.Combine(dataDir, HostKeyFileName);
    }

    /// <summary>启动监听；未启用时仅记录日志。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enable)
        {
            _logger.LogInformation("SFTP server disabled (SFTP:Enable=false)");
            return Task.CompletedTask;
        }

        var (address, port) = FtpListenParser.Parse(_config.Listen);
        if (!IPAddress.TryParse(address, out var ip))
            ip = IPAddress.Any;

        _credentials = new SshServerCredentials(LoadOrCreateHostKey());

        var listener = new TcpListener(ip, port);
        listener.Start();
        _listener = listener;

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);

        _logger.LogInformation("SFTP server started on {Address}:{Port}", ip, port);
        return Task.CompletedTask;
    }

    /// <summary>停止监听并关闭所有会话。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enable)
            return;

        _logger.LogInformation("Stopping SFTP server");
        _cts?.Cancel();
        _listener?.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // 监听已停止。
            }
        }

        foreach (var session in _sessions.Values)
        {
            try
            {
                await session.CloseAsync(SshDisconnectReason.ByApplication, "server shutting down");
            }
            catch
            {
                // 会话可能已断开。
            }
            session.Dispose();
        }
        _sessions.Clear();
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _logger.LogInformation("SFTP server stopped");
    }

    /// <summary>
    /// 读取持久化的主机密钥；文件不存在或内容无法解析时生成新密钥并写盘。
    /// 解析失败只降级（告警 + 重新生成），不能因为一个坏文件让 SFTP 起不来。
    /// </summary>
    private IKeyPair LoadOrCreateHostKey()
    {
        if (File.Exists(_hostKeyPath))
        {
            try
            {
                var key = LoadHostKey(File.ReadAllText(_hostKeyPath));
                _logger.LogInformation(
                    "SFTP host key loaded from {Path} (SHA256:{Fingerprint})",
                    _hostKeyPath, Fingerprint(key));
                return key;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to load SFTP host key from {Path}; generating a new one", _hostKeyPath);
            }
        }

        var generated = CreateHostKey();
        try
        {
            SaveHostKey(generated);
            _logger.LogInformation(
                "SFTP host key generated and saved to {Path} (SHA256:{Fingerprint})",
                _hostKeyPath, Fingerprint(generated));
        }
        catch (Exception ex)
        {
            // 写盘失败不影响本次运行，只是下次启动会再生成一个（指纹又会变）。
            _logger.LogWarning(ex, "failed to persist SFTP host key to {Path}", _hostKeyPath);
        }
        return generated;
    }

    /// <summary>生成主机密钥：优先 ECDSA P-256，环境不支持时回退 RSA-2048。</summary>
    private static IKeyPair CreateHostKey()
    {
        try
        {
            return SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair(null);
        }
        catch
        {
            return SshAlgorithms.PublicKey.RsaWithSha256.GenerateKeyPair(2048);
        }
    }

    /// <summary>把主机密钥以 PKCS#8 PEM 写盘；Unix 下收紧到 600（私钥不能给别人读）。</summary>
    private void SaveHostKey(IKeyPair keyPair)
    {
        string pem;
        switch (keyPair)
        {
            case Microsoft.DevTunnels.Ssh.Algorithms.ECDsa.KeyPair ec:
            {
                using var algorithm = System.Security.Cryptography.ECDsa.Create(ec.ExportParameters(true));
                pem = algorithm.ExportPkcs8PrivateKeyPem();
                break;
            }
            case Microsoft.DevTunnels.Ssh.Algorithms.Rsa.KeyPair rsa:
            {
                using var algorithm = System.Security.Cryptography.RSA.Create(rsa.ExportParameters(true));
                pem = algorithm.ExportPkcs8PrivateKeyPem();
                break;
            }
            default:
                throw new NotSupportedException($"unsupported host key type {keyPair.GetType().Name}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_hostKeyPath)!);
        File.WriteAllText(_hostKeyPath, pem);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_hostKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>从 PKCS#8 PEM 还原主机密钥（先按 ECDSA 解析，再按 RSA）。</summary>
    private static IKeyPair LoadHostKey(string pem)
    {
        try
        {
            using var ec = System.Security.Cryptography.ECDsa.Create();
            ec.ImportFromPem(pem);
            var pair = new Microsoft.DevTunnels.Ssh.Algorithms.ECDsa.KeyPair();
            pair.ImportParameters(NormalizeCurve(ec.ExportParameters(true)));
            return pair;
        }
        catch (ArgumentException)
        {
            // 不是 ECDSA 密钥，继续尝试 RSA。
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // 同上：格式不符或只有公钥。
        }

        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(pem);
        var rsaPair = new Microsoft.DevTunnels.Ssh.Algorithms.Rsa.KeyPair();
        rsaPair.ImportParameters(rsa.ExportParameters(true));
        return rsaPair;
    }

    /// <summary>
    /// 补全曲线信息。DevTunnels 的 <c>ImportParameters</c> 要求 <c>Curve.Oid.FriendlyName</c> 非空，
    /// 而某些平台上 <c>ExportParameters</c> 只给 OID 值 —— 按位长回填具名曲线。
    /// </summary>
    private static System.Security.Cryptography.ECParameters NormalizeCurve(
        System.Security.Cryptography.ECParameters parameters)
    {
        if (!string.IsNullOrEmpty(parameters.Curve.Oid?.FriendlyName))
            return parameters;

        parameters.Curve = parameters.Curve.Oid?.Value switch
        {
            "1.2.840.10045.3.1.7" => System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
            "1.3.132.0.34" => System.Security.Cryptography.ECCurve.NamedCurves.nistP384,
            "1.3.132.0.35" => System.Security.Cryptography.ECCurve.NamedCurves.nistP521,
            _ => parameters.Curve,
        };
        return parameters;
    }

    /// <summary>OpenSSH 风格指纹：SHA256(公钥 blob) 的 base64（去掉填充）。</summary>
    private static string Fingerprint(IKeyPair keyPair)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(keyPair.GetPublicKeyBytes().ToArray());
        return Convert.ToBase64String(hash).TrimEnd('=');
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SFTP accept failed");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _sessionSequence);
        User? authenticated = null;
        SshServerSession? session = null;

        using (client)
        {
            try
            {
                session = new SshServerSession(new SshSessionConfiguration(), _trace)
                {
                    Credentials = _credentials,
                    ConnectTimeout = TimeSpan.FromSeconds(20),
                };
                _sessions[id] = session;

                session.Authenticating += (_, e) => e.AuthenticationTask = AuthenticateAsync(e, user => authenticated = user);
                session.ChannelOpening += (_, e) =>
                {
                    if (authenticated is null)
                    {
                        e.FailureReason = SshChannelOpenFailureReason.AdministrativelyProhibited;
                        return;
                    }
                    if (!string.Equals(e.Request.ChannelType, "session", StringComparison.Ordinal))
                    {
                        e.FailureReason = SshChannelOpenFailureReason.UnknownChannelType;
                        return;
                    }
                    e.OpeningTask = HandleSessionChannelAsync(e.Channel, authenticated, ct);
                };

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Closed += (_, _) => closed.TrySetResult();

                await using var stream = client.GetStream();
                await session.ConnectAsync(stream, ct);
                await closed.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 服务停止或客户端断开。
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SFTP session failed");
            }
            finally
            {
                _sessions.TryRemove(id, out _);
                session?.Dispose();
            }
        }
    }

    /// <summary>
    /// 密码认证：与 FTP 相同 —— 用户存在、未禁用、密码校验通过即放行；
    /// 公钥查询与其他方式一律拒绝（当前只支持密码登录）。
    /// </summary>
    private async Task<ClaimsPrincipal?> AuthenticateAsync(SshAuthenticatingEventArgs e, Action<User> onSuccess)
    {
        if (e.AuthenticationType != SshAuthenticationType.ClientPassword)
            return null;
        if (string.IsNullOrEmpty(e.Username) || string.IsNullOrEmpty(e.Password))
            return null;

        try
        {
            using var scope = _scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var resp = await users.GetByNameAsync(e.Username, CancellationToken.None);
            if (resp.Code != RespCodes.Ok || resp.Data is null || resp.Data.Disabled)
                return null;
            if (!UserService.VerifyPassword(e.Password, resp.Data.PasswordHash))
                return null;

            onSuccess(resp.Data);

            var identity = new ClaimsIdentity("Sftp");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resp.Data.Id.ToString()));
            identity.AddClaim(new Claim(ClaimTypes.Name, resp.Data.Username));
            identity.AddClaim(new Claim(ClaimTypes.Role, resp.Data.Role.ToString("D")));
            return new ClaimsPrincipal(identity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SFTP authentication failed for {Username}", e.Username);
            return null;
        }
    }

    /// <summary>接受会话通道；客户端请求 <c>subsystem</c> 时启动 SFTP 协议处理。</summary>
    private Task HandleSessionChannelAsync(SshChannel channel, User user, CancellationToken ct)
    {
        var fileSystem = new SftpSessionFileSystem(_mount, user, _scopes);
        channel.Closed += (_, _) => fileSystem.Dispose();

        channel.Request += (_, e) =>
        {
            // 只认 subsystem（SFTP）；其余请求（pty/shell/exec）一律拒绝。
            if (!string.Equals(e.RequestType, "subsystem", StringComparison.Ordinal))
                return;

            e.IsAuthorized = true;
            var stream = new SshStream(channel);
            _ = Task.Run(async () =>
            {
                using var server = new SFTPServer(stream, stream, fileSystem, _trace, 32 * 1024);
                try
                {
                    await server.Run(ct);
                }
                catch (OperationCanceledException)
                {
                    // 会话结束。
                }
                catch (EndOfStreamException)
                {
                    // 客户端断开导致通道结束 —— 正常的收尾路径。
                }
                catch (SshConnectionException)
                {
                    // 对端直接断开 TCP（reset / 网络中断），SFTP 会话随之结束。
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SFTP subsystem ended with error");
                }
            }, CancellationToken.None);
        };

        return Task.CompletedTask;
    }
}
