using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenList.Server.Hosting;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// FTP 设置快照的解析与证书加载（FubarDev 挂点本身很薄，行为都在纯函数里，见 plan §55）。
/// </summary>
[Timeout(30_000)]
public class FtpServerSettingsTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ------------------------------------------------------------ ftp_public_host

    [Test]
    public async Task PublicHost_IpLiteral_IsAccepted()
    {
        var ip = FtpServerSettings.ParsePublicHost("203.0.113.7", Logger);
        await Assert.That(ip).IsEqualTo(IPAddress.Parse("203.0.113.7"));
    }

    [Test]
    public async Task PublicHost_DomainName_IsRejected()
    {
        // PASV 应答必须是 IP 字面量：域名交给客户端解析的兼容性太差，宁可明确拒绝。
        var ip = FtpServerSettings.ParsePublicHost("ftp.example.com", Logger);
        await Assert.That(ip).IsNull();
    }

    [Test]
    public async Task PublicHost_Empty_IsNull()
    {
        await Assert.That(FtpServerSettings.ParsePublicHost("", Logger)).IsNull();
        await Assert.That(FtpServerSettings.ParsePublicHost("  ", Logger)).IsNull();
    }

    // ------------------------------------------------------------ ftp_pasv_port_range

    [Test]
    public async Task PortRange_Empty_IsUnset()
    {
        await Assert.That(FtpServerSettings.ParsePortRange("", Logger)).IsEqualTo((0, 0));
    }

    [Test]
    public async Task PortRange_SinglePort_IsMinEqualsMax()
    {
        await Assert.That(FtpServerSettings.ParsePortRange("50000", Logger)).IsEqualTo((50000, 50000));
    }

    [Test]
    public async Task PortRange_Span_IsParsed()
    {
        await Assert.That(FtpServerSettings.ParsePortRange("50000-50100", Logger)).IsEqualTo((50000, 50100));
    }

    [Test]
    public async Task PortRange_Garbage_IsIgnoredNotFatal()
    {
        await Assert.That(FtpServerSettings.ParsePortRange("abc", Logger)).IsEqualTo((0, 0));
        await Assert.That(FtpServerSettings.ParsePortRange("0-100", Logger)).IsEqualTo((0, 0));
        await Assert.That(FtpServerSettings.ParsePortRange("70000-70010", Logger)).IsEqualTo((0, 0));
        await Assert.That(FtpServerSettings.ParsePortRange("50100-50000", Logger)).IsEqualTo((0, 0));
    }

    // ------------------------------------------------------------ 证书

    [Test]
    public async Task Certificate_NoneConfigured_IsNull()
    {
        await Assert.That(FtpServerSettings.LoadCertificate("", "", Logger)).IsNull();
    }

    [Test]
    public async Task Certificate_HalfConfigured_IsNull()
    {
        await Assert.That(FtpServerSettings.LoadCertificate("/tmp/cert.pem", "", Logger)).IsNull();
    }

    [Test]
    public async Task Certificate_MissingFile_IsNull()
    {
        // 配错路径不能把 FTP 服务打死：以明文继续，AUTH TLS 由库回 500。
        await Assert.That(FtpServerSettings.LoadCertificate("/no/such/cert.pem", "/no/such/key.pem", Logger)).IsNull();
    }

    [Test]
    public async Task Certificate_PemPair_LoadsWithPrivateKey()
    {
        var (certPem, keyPem) = GenerateSelfSignedPem();
        var dir = Path.Combine(Path.GetTempPath(), "openlist-ftp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "cert.pem");
        var keyPath = Path.Combine(dir, "key.pem");
        await File.WriteAllTextAsync(certPath, certPem);
        await File.WriteAllTextAsync(keyPath, keyPem);

        try
        {
            var cert = FtpServerSettings.LoadCertificate(certPath, keyPath, Logger);
            await Assert.That(cert).IsNotNull();
            // SslStream 握手必须有私钥。
            await Assert.That(((X509Certificate2)cert!).HasPrivateKey).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Certificate_Pfx_LoadsWithPrivateKey()
    {
        var (certPem, keyPem) = GenerateSelfSignedPem();
        using var pem = X509Certificate2.CreateFromPem(certPem, keyPem);
        var pfxPath = Path.Combine(Path.GetTempPath(), "openlist-ftp-test-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(pfxPath, pem.Export(X509ContentType.Pkcs12));

        try
        {
            var cert = FtpServerSettings.LoadCertificate(pfxPath, "", Logger);
            await Assert.That(cert).IsNotNull();
            await Assert.That(((X509Certificate2)cert!).HasPrivateKey).IsTrue();
        }
        finally
        {
            File.Delete(pfxPath);
        }
    }

    // ------------------------------------------------------------ 快照

    [Test]
    public async Task FromSettings_TogglesAndSnapshot()
    {
        var dict = new Dictionary<string, string>
        {
            ["ftp_public_host"] = "198.51.100.1",
            ["ftp_pasv_port_range"] = "50000-50050",
            ["ftp_mandatory_tls"] = "true",
            ["ftp_implicit_tls"] = "false",
        };
        var s = FtpServerSettings.FromSettings(dict, Logger);
        await Assert.That(s.PublicAddress).IsEqualTo(IPAddress.Parse("198.51.100.1"));
        await Assert.That(s.HasPortRange).IsTrue();
        await Assert.That(s.PasvMinPort).IsEqualTo(50000);
        await Assert.That(s.MandatoryTls).IsTrue();
        await Assert.That(s.ImplicitTls).IsFalse();
    }

    [Test]
    public async Task FromSettings_Empty_IsAllDefaults()
    {
        var s = FtpServerSettings.FromSettings(new Dictionary<string, string>(), Logger);
        await Assert.That(s.PublicAddress).IsNull();
        await Assert.That(s.HasPortRange).IsFalse();
        await Assert.That(s.MandatoryTls).IsFalse();
        await Assert.That(s.ImplicitTls).IsFalse();
        await Assert.That(s.Certificate).IsNull();
    }

    // ------------------------------------------------------------ 测试辅助

    /// <summary>运行时生成一张自签证书的 PEM 对（证书 + EC 私钥）。</summary>
    private static (string cert, string key) GenerateSelfSignedPem()
    {
        using var ecdsa = ECDsa.Create();
        var req = new CertificateRequest("CN=openlist-ftp-test", ecdsa, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (cert.ExportCertificatePem(), ecdsa.ExportPkcs8PrivateKeyPem());
    }
}
