using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using FubarDev.FtpServer;
using FubarDev.FtpServer.Authentication;
using FubarDev.FtpServer.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenList.Server.Hosting;

/// <summary>
/// 容忍「通道已关闭」的 SSL 包装工厂。
/// </summary>
/// <remarks>
/// FubarDev 默认实现在关闭数据通道时会调 <see cref="SslStream.ShutdownAsync"/>，
/// 但此时底层 duplex pipe 往往已经 shutdown —— 抛出的
/// <see cref="InvalidOperationException"/>（"Write operations are not allowed after the channel
/// was shutdown"）会顺着 <c>SendResponsesAsync</c> 把<b>控制连接</b>一起带走
/// （实测：PROT P 上传后客户端 QUIT 直接收到 EOF）。数据本身已经完整落盘，
/// 这里只在关闭时吞掉这两类关闭竞态异常，其余行为全部透传默认实现。
/// </remarks>
public sealed class TolerantSslStreamWrapperFactory : ISslStreamWrapperFactory
{
    private readonly DefaultSslStreamWrapperFactory _inner;

    public TolerantSslStreamWrapperFactory(ILogger<DefaultSslStreamWrapperFactory> logger)
        => _inner = new DefaultSslStreamWrapperFactory(logger);

    /// <inheritdoc />
    public Task<Stream> WrapStreamAsync(Stream stream, bool isClient, X509Certificate certificate, CancellationToken cancellationToken)
        => _inner.WrapStreamAsync(stream, isClient, certificate, cancellationToken);

    /// <inheritdoc />
    public async Task CloseStreamAsync(Stream sslStream, CancellationToken cancellationToken)
    {
        try
        {
            await _inner.CloseStreamAsync(sslStream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 关闭竞态：连接关停顺序不受我们控制，忽略。
        }
    }
}

/// <summary>
/// 把网站设置里的 PASV 参数（对外地址 + 端口段）交给 FubarDev.FtpServer。
/// </summary>
/// <remarks>
/// FubarDev 的默认实现 <c>SimplePasvAddressResolver</c> 只返回空的 <see cref="PasvListenerOptions"/>，
/// 公网地址（NAT 后部署必需）和端口段没有任何配置入口 —— 本实现覆盖它，数据来自设置分组 10。
/// 注册顺序在 <c>AddFtpServer</c> 的默认注册之后，后注册者胜出。
/// </remarks>
public sealed class SettingsPasvAddressResolver : IPasvAddressResolver
{
    private readonly FtpServerSettingsSource _settings;

    public SettingsPasvAddressResolver(FtpServerSettingsSource settings) => _settings = settings;

    /// <inheritdoc />
    public Task<PasvListenerOptions> GetOptionsAsync(
        IFtpConnection connection,
        AddressFamily? addressFamily,
        CancellationToken cancellationToken)
    {
        var s = _settings.Get();
        // 未配置 ftp_public_host 时 PublicAddress 为 null —— 相当于 FubarDev 默认解析器的
        // 「不做改写、用连接本地地址」，是受支持的取值，这里只是补上可空标注。
        var options = new PasvListenerOptions(
            s.HasPortRange ? s.PasvMinPort : 0,
            s.HasPortRange ? s.PasvMaxPort : 0,
            s.PublicAddress!);
        return Task.FromResult(options);
    }
}

/// <summary>
/// 把 <c>ftp_tls_*_path</c> 加载出的证书交给 <c>AUTH TLS</c> 命令处理（显式 FTPS）。
/// </summary>
/// <remarks>
/// FubarDev 从 <see cref="AuthTlsOptions.ServerCertificate"/> 取证书
/// （<c>TlsAuthenticationMechanism</c> → <c>IFtpHostSelector.SelectedHost.Certificate</c>），
/// 没有证书时 AUTH TLS/PBSZ/PROT 直接回 500 且不出现在 FEAT 里。
/// </remarks>
public sealed class SettingsAuthTlsOptionsConfigurator : IConfigureOptions<AuthTlsOptions>
{
    private readonly FtpServerSettingsSource _settings;

    public SettingsAuthTlsOptionsConfigurator(FtpServerSettingsSource settings) => _settings = settings;

    /// <inheritdoc />
    public void Configure(AuthTlsOptions options)
    {
        var s = _settings.Get();
        options.ServerCertificate = s.Certificate;
        options.ImplicitFtps = s.ImplicitTls && s.Certificate is not null;
    }
}

/// <summary>
/// 隐式 FTPS（<c>ftp_implicit_tls=true</c>）：控制连接建立即 TLS 握手，明文客户端无法使用该端口。
/// </summary>
/// <remarks>
/// 照 FubarDev 自带 <c>UseImplicitTls</c> 的实现模式（控制流适配器 + 连接配置器），
/// 但证书改从设置懒加载 —— 因为隐式模式必须在注册期就挂适配器，而证书在数据库里，
/// 注册时（<c>Program.cs</c>）库还没初始化。
/// </remarks>
public sealed class SettingsImplicitTlsStreamAdapter : IFtpControlStreamAdapter
{
    private readonly FtpServerSettingsSource _settings;
    private readonly ISslStreamWrapperFactory _ssl;

    public SettingsImplicitTlsStreamAdapter(FtpServerSettingsSource settings, ISslStreamWrapperFactory ssl)
    {
        _settings = settings;
        _ssl = ssl;
    }

    /// <inheritdoc />
    public async Task<Stream> WrapAsync(Stream stream, CancellationToken cancellationToken)
    {
        var s = _settings.Get();
        if (!s.ImplicitTls || s.Certificate is null) return stream;
        return await _ssl.WrapStreamAsync(stream, false, s.Certificate, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>隐式 FTPS 下自动激活 TLS 认证机制（等价于客户端一上来就发了 AUTH TLS）。</summary>
public sealed class SettingsImplicitTlsConnectionConfigurator : IFtpConnectionConfigurator
{
    private readonly FtpServerSettingsSource _settings;

    public SettingsImplicitTlsConnectionConfigurator(FtpServerSettingsSource settings) => _settings = settings;

    /// <inheritdoc />
    public Task Configure(IFtpConnection connection, CancellationToken cancellationToken)
    {
        var s = _settings.Get();
        if (!s.ImplicitTls || s.Certificate is null) return Task.CompletedTask;

        // FtpConnection 在构造时始终挂载 IServiceProvidersFeature（见 FubarDev 的
        // FtpConnection.ServiceProvidersFeature），取不到即为库契约被破坏。
        var serviceProvider = connection.Features.Get<IServiceProvidersFeature>()!.RequestServices;
        var stateMachine = serviceProvider.GetRequiredService<IFtpLoginStateMachine>();
        var authTlsMechanism = serviceProvider
            .GetRequiredService<IEnumerable<IAuthenticationMechanism>>()
            .Single(x => x.CanHandle("TLS"));
        stateMachine.Activate(authTlsMechanism);
        return Task.CompletedTask;
    }
}
