using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenList.Server.SftpServer;

/// <summary>
/// Manages SSH host keys for the SFTP server.
/// Generates RSA host key on first run and persists to data/ssh/ directory.
/// Go: server/sftp/hostkey.go
/// </summary>
public static class SshHostKeyManager
{
    private static RSA? _hostKey;
    private static readonly object _lock = new();

    /// <summary>Get or generate the RSA host key pair.</summary>
    public static RSA GetOrGenerateHostKey(string dataDir)
    {
        if (_hostKey != null) return _hostKey;
        lock (_lock)
        {
            if (_hostKey != null) return _hostKey;

            var sshDir = Path.Combine(dataDir, "ssh");
            Directory.CreateDirectory(sshDir);

            var privateKeyPath = Path.Combine(sshDir, "ssh_host_rsa_key");
            var publicKeyPath = Path.Combine(sshDir, "ssh_host_rsa_key.pub");

            // Try loading existing key
            if (File.Exists(privateKeyPath))
            {
                try
                {
                    var pem = File.ReadAllText(privateKeyPath);
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    _hostKey = rsa;
                    return _hostKey;
                }
                catch
                {
                    // Key file corrupted, regenerate
                }
            }

            // Generate new RSA 4096-bit key
            var newKey = RSA.Create(4096);
            _hostKey = newKey;

            // Save private key as PEM
            var privatePem = newKey.ExportRSAPrivateKeyPem();
            File.WriteAllText(privateKeyPath, privatePem);

            // Save public key in OpenSSH format
            try
            {
                var publicKey = newKey.ExportRSAPublicKey();
                var sshPubKey = FormatSshPublicKey(newKey);
                File.WriteAllText(publicKeyPath, sshPubKey);
            }
            catch
            {
                // Non-critical if public key export fails
            }

            return _hostKey;
        }
    }

    /// <summary>Format RSA public key in OpenSSH authorized_keys format.</summary>
    private static string FormatSshPublicKey(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);

        // SSH RSA public key format: "ssh-rsa" + string(e) + string(n)
        using var ms = new MemoryStream();
        WriteSshString(ms, "ssh-rsa"u8);
        WriteSshBigInt(ms, parameters.Exponent!);
        WriteSshBigInt(ms, parameters.Modulus!);

        var blob = ms.ToArray();
        return $"ssh-rsa {Convert.ToBase64String(blob)} openlist-host-key";
    }

    private static void WriteSshString(Stream s, ReadOnlySpan<byte> value)
    {
        WriteSshUInt32(s, (uint)value.Length);
        s.Write(value);
    }

    private static void WriteSshBigInt(Stream s, byte[] value)
    {
        // Add leading zero byte if high bit is set (positive number in SSH format)
        if (value.Length > 0 && (value[0] & 0x80) != 0)
        {
            WriteSshUInt32(s, (uint)(value.Length + 1));
            s.WriteByte(0);
        }
        else
        {
            WriteSshUInt32(s, (uint)value.Length);
        }
        s.Write(value);
    }

    private static void WriteSshUInt32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }
}
