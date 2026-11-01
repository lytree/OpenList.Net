using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;

namespace OpenList.Server.Hosting;

/// <summary>
/// FTP 服务器从网站设置（分组 10）读出的运行参数快照。
/// </summary>
/// <remarks>
/// 这些参数在 FubarDev.FtpServer 内部是按连接/按 PASV 请求消费的，
/// 快照只读一次并缓存：<b>改设置后需要重启 FTP 服务（或进程）生效</b>。
/// </remarks>
public sealed record FtpServerSettings
{
    /// <summary>未配置时的空快照（全部参数取默认行为）。</summary>
    public static readonly FtpServerSettings Empty = new();

    /// <summary>PASV 应答里向外公布的地址（<see cref="FtpServerSettings"/> 的 <c>ftp_public_host</c>）。</summary>
    public IPAddress? PublicAddress { get; init; }

    /// <summary>PASV 数据端口下界（<c>ftp_pasv_port_range</c>）。</summary>
    public int PasvMinPort { get; init; }

    /// <summary>PASV 数据端口上界。</summary>
    public int PasvMaxPort { get; init; }

    /// <summary>控制通道未加密时拒绝登录（<c>ftp_mandatory_tls</c>）。</summary>
    public bool MandatoryTls { get; init; }

    /// <summary>控制通道建立即 TLS 握手、不再支持明文（<c>ftp_implicit_tls</c>）。</summary>
    public bool ImplicitTls { get; init; }

    /// <summary>服务器证书（由两个 <c>ftp_tls_*_path</c> 加载；加载失败为 null，FTP 以明文继续）。</summary>
    public X509Certificate? Certificate { get; init; }

    /// <summary>是否配置了 PASV 端口段。</summary>
    public bool HasPortRange => PasvMinPort > 0 && PasvMaxPort >= PasvMinPort;

    /// <summary>
    /// 从设置字典解析快照。非法输入一律<b>告警后按未配置处理</b>，绝不让 FTP 起不来。
    /// </summary>
    public static FtpServerSettings FromSettings(IReadOnlyDictionary<string, string> dict, ILogger log)
    {
        var host = ParsePublicHost(dict.GetValueOrDefault("ftp_public_host") ?? "", log);
        var (minPort, maxPort) = ParsePortRange(dict.GetValueOrDefault("ftp_pasv_port_range") ?? "", log);
        var certificate = LoadCertificate(
            dict.GetValueOrDefault("ftp_tls_public_cert_path") ?? "",
            dict.GetValueOrDefault("ftp_tls_private_key_path") ?? "", log);

        return new FtpServerSettings
        {
            PublicAddress = host,
            PasvMinPort = minPort,
            PasvMaxPort = maxPort,
            MandatoryTls = bool.TryParse(dict.GetValueOrDefault("ftp_mandatory_tls"), out var m) && m,
            ImplicitTls = bool.TryParse(dict.GetValueOrDefault("ftp_implicit_tls"), out var i) && i,
            Certificate = certificate,
        };
    }

    /// <summary>解析对外公布地址：只接受 IP 字面量（PASV 应答需要 IP，域名解析失败模式太多）。</summary>
    internal static IPAddress? ParsePublicHost(string value, ILogger log)
    {
        var v = value.Trim();
        if (v.Length == 0) return null;
        if (IPAddress.TryParse(v, out var ip)) return ip;
        log.LogWarning("FTP: ftp_public_host={Host} 不是合法 IP 地址（PASV 应答需要 IP，不接受域名），已忽略", v);
        return null;
    }

    /// <summary>解析 PASV 端口段：空表示未配置；支持 "50000"（单端口）与 "50000-50100"（区间）。</summary>
    internal static (int min, int max) ParsePortRange(string value, ILogger log)
    {
        var v = value.Trim();
        if (v.Length == 0) return (0, 0);

        var parts = v.Split('-', 2);
        if (!int.TryParse(parts[0].Trim(), out var min) || min < 1 || min > 65535)
        {
            log.LogWarning("FTP: ftp_pasv_port_range={Range} 起始端口非法，已忽略（期望 \"50000\" 或 \"50000-50100\"）", v);
            return (0, 0);
        }

        var max = min;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1].Trim(), out max) || max < 1 || max > 65535)
            {
                log.LogWarning("FTP: ftp_pasv_port_range={Range} 结束端口非法，已忽略", v);
                return (0, 0);
            }
        }

        if (max < min)
        {
            log.LogWarning("FTP: ftp_pasv_port_range={Range} 上界小于下界，已忽略", v);
            return (0, 0);
        }

        return (min, max);
    }

    /// <summary>加载服务器证书：PEM（证书 + 私钥分开）或 PFX（二进制，单文件）。</summary>
    internal static X509Certificate? LoadCertificate(string publicCertPath, string privateKeyPath, ILogger log)
    {
        var pub = publicCertPath.Trim();
        var key = privateKeyPath.Trim();
        if (pub.Length == 0 && key.Length == 0) return null;

        // PFX/P12 自带私钥，不需要单独的私钥路径；PEM 则必须两个都填。
        var isPfx = pub.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
                    || pub.EndsWith(".p12", StringComparison.OrdinalIgnoreCase);
        if (pub.Length == 0 || (!isPfx && key.Length == 0))
        {
            log.LogWarning("FTP: PEM 证书需要同时填写证书路径与私钥路径（当前只填了一半），FTPS 未启用");
            return null;
        }

        try
        {
            X509Certificate2 cert;
            if (isPfx)
            {
                cert = new X509Certificate2(pub);
            }
            else
            {
                cert = X509Certificate2.CreateFromPemFile(pub, key);
                if (OperatingSystem.IsWindows())
                {
                    // Windows 的 SslStream 拒绝临时密钥：转成 PKCS#12 再加载。
                    using (cert)
                        cert = new X509Certificate2(cert.Export(X509ContentType.Pkcs12));
                }
            }

            if (!cert.HasPrivateKey)
            {
                log.LogWarning("FTP: 证书 {Cert} 没有私钥，无法完成 TLS 握手，FTPS 未启用", pub);
                return null;
            }

            log.LogInformation("FTP: FTPS 证书已加载 {Subject}（指纹 {Thumbprint}）", cert.Subject, cert.Thumbprint);
            return cert;
        }
        catch (Exception ex)
        {
            // 证书配错不该把整个 FTP 服务打死：以明文继续，AUTH TLS 由库回 500。
            log.LogWarning(ex, "FTP: 加载 FTPS 证书失败（cert={Cert} key={Key}），FTPS 未启用", pub, key);
            return null;
        }
    }
}

/// <summary>
/// <see cref="FtpServerSettings"/> 的单例持有者：首次访问时读一次设置并缓存。
/// </summary>
/// <remarks>
/// 刻意懒加载：FTP 关闭（默认）时永远不会读设置，测试工厂也不会碰数据库。
/// 消费方都在 FTP 已启动之后（hosted service / 首个连接），此时库已完成初始化。
/// </remarks>
public sealed class FtpServerSettingsSource
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FtpServerSettingsSource> _log;
    private readonly object _lock = new();
    private volatile FtpServerSettings? _cache;

    public FtpServerSettingsSource(IServiceScopeFactory scopes, ILogger<FtpServerSettingsSource> log)
    {
        _scopes = scopes;
        _log = log;
    }

    /// <summary>当前快照（首次调用读取设置并缓存，之后不再变化）。</summary>
    public FtpServerSettings Get()
    {
        var cached = _cache;
        if (cached is not null) return cached;
        lock (_lock)
        {
            if (_cache is not null) return _cache;
            return _cache = Load();
        }
    }

    private FtpServerSettings Load()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetService<ISettingService>();
            if (settings is null) return FtpServerSettings.Empty;

            var resp = settings.ListAsync().GetAwaiter().GetResult();
            if (resp.Code != 200 || resp.Data is null)
            {
                _log.LogWarning("FTP: 读取设置失败（code={Code}），FTP 高级参数全部按未配置处理", resp.Code);
                return FtpServerSettings.Empty;
            }

            var dict = resp.Data.ToDictionary(s => s.Key, s => s.Value ?? string.Empty, StringComparer.Ordinal);
            var snapshot = FtpServerSettings.FromSettings(dict, _log);
            if (snapshot.HasPortRange)
                _log.LogInformation("FTP: PASV 端口段 {Min}-{Max}，对外地址 {Addr}",
                    snapshot.PasvMinPort, snapshot.PasvMaxPort, snapshot.PublicAddress?.ToString() ?? "(默认)");
            if (snapshot.MandatoryTls && snapshot.Certificate is null)
                _log.LogWarning("FTP: ftp_mandatory_tls=true 但没有可用证书 —— 所有登录都会被拒绝，请先配置 ftp_tls_* 路径");
            return snapshot;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FTP: 读取设置异常，FTP 高级参数全部按未配置处理");
            return FtpServerSettings.Empty;
        }
    }
}
