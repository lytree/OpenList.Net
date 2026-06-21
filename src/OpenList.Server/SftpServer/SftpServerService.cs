using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Server.SftpServer;

/// <summary>
/// SFTP server hosted service.
/// Implements a minimal SSH-2 server with SFTP subsystem support.
/// Handles: version exchange, key exchange, user authentication, SFTP channel operations.
/// Go: server/sftp.go SftpDriver + sftpd-openlist
///
/// NOTE: This is a minimal SSH server implementation. For production use with full SSH
/// protocol compliance, consider using a dedicated SSH server library.
/// </summary>
public class SftpServerService : IHostedService, IDisposable
{
    private readonly AppConfiguration _config;
    private readonly ILogger<SftpServerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SftpAuthHandler _authHandler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public SftpServerService(
        AppConfiguration config,
        ILogger<SftpServerService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _config = config;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _authHandler = new SftpAuthHandler(scopeFactory, logger is ILogger<SftpAuthHandler> al ? al
            : new LoggerFactory().CreateLogger<SftpAuthHandler>());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.SFTP.Enable)
        {
            _logger.LogInformation("SFTP server is disabled");
            return Task.CompletedTask;
        }

        var (address, port) = ParseListenAddress(_config.SFTP.Listen);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Ensure host key exists
        SshHostKeyManager.GetOrGenerateHostKey("data");

        _listener = new TcpListener(
            string.IsNullOrEmpty(address) ? IPAddress.Any : IPAddress.Parse(address), port);
        _listener.Start();

        _acceptTask = AcceptClientsAsync(_cts.Token);
        _logger.LogInformation("SFTP server started on {Address}:{Port}", address, port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener?.Stop();

        if (_acceptTask != null)
        {
            try { await _acceptTask; } catch (OperationCanceledException) { }
        }

        _logger.LogInformation("SFTP server stopped");
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                var remoteIp = remoteEp?.Address.ToString() ?? "unknown";
                _ = HandleClientAsync(client, remoteIp, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting SFTP client");
            }
        }
    }

    /// <summary>
    /// Handle a single SFTP client connection.
    /// Implements minimal SSH-2 protocol: version exchange → key exchange → auth → channel → SFTP.
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, string remoteIp, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            stream.ReadTimeout = 30000; // 30s for initial handshake

            // 1. SSH version exchange
            await SendLineAsync(stream, "SSH-2.0-OpenList_SFTP", ct);
            var clientVersion = await ReadLineAsync(stream, ct);
            if (clientVersion == null || !clientVersion.StartsWith("SSH-2.0-"))
            {
                _logger.LogWarning("[SFTP] {IP}: Invalid SSH version: {Version}", remoteIp, clientVersion);
                return;
            }

            // 2. Key exchange init (send our KEXINIT)
            var hostKey = SshHostKeyManager.GetOrGenerateHostKey("data");
            await SendKexInitAsync(stream, hostKey, ct);

            // 3. Read client KEXINIT
            var clientKexInit = await ReadSshPacketAsync(stream, ct);
            if (clientKexInit == null)
            {
                _logger.LogWarning("[SFTP] {IP}: Failed to read client KEXINIT", remoteIp);
                return;
            }

            // 4. Perform Diffie-Hellman key exchange (simplified)
            // For a full implementation, we'd negotiate algorithms and perform proper DH exchange.
            // This minimal implementation sends the host key and signals that the client should
            // proceed with password authentication.
            await PerformKeyExchangeAsync(stream, hostKey, ct);

            // 5. Read authentication request
            var (user, error) = await HandleAuthenticationAsync(stream, remoteIp, ct);
            if (user == null)
            {
                _logger.LogWarning("[SFTP] {IP}: Authentication failed: {Error}", remoteIp, error);
                await SendAuthFailureAsync(stream, ct);
                return;
            }

            _logger.LogInformation("[SFTP] {User}({IP}) authenticated successfully", user.Username, remoteIp);

            // 6. Handle SFTP channel
            await HandleSftpSessionAsync(stream, user, remoteIp, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "[SFTP] {IP}: Connection ended", remoteIp);
        }
    }

    // ── SSH Protocol Helpers ──────────────────────────────────────────

    private async Task SendLineAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(line + "\r\n");
        await stream.WriteAsync(data, ct);
    }

    private async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        int pos = 0;
        while (pos < buffer.Length)
        {
            var b = new byte[1];
            var read = await stream.ReadAsync(b, ct);
            if (read == 0) return null;
            if (b[0] == '\n')
            {
                var line = Encoding.UTF8.GetString(buffer, 0, pos).TrimEnd('\r');
                return line;
            }
            buffer[pos++] = b[0];
        }
        return null;
    }

    private async Task SendKexInitAsync(NetworkStream stream, RSA hostKey, CancellationToken ct)
    {
        // Build SSH_MSG_KEXINIT packet
        using var packet = new MemoryStream();
        // packet_type = 20 (SSH_MSG_KEXINIT)
        WriteByte(packet, 20);
        // cookie (16 random bytes)
        var cookie = new byte[16];
        RandomNumberGenerator.Fill(cookie);
        packet.Write(cookie);
        // kex_algorithms
        WriteSshString(packet, "curve25519-sha256,diffie-hellman-group14-sha256");
        // server_host_key_algorithms
        WriteSshString(packet, "rsa-sha2-256,ssh-rsa");
        // encryption_algorithms_client_to_server
        WriteSshString(packet, "aes128-ctr,aes256-ctr");
        // encryption_algorithms_server_to_client
        WriteSshString(packet, "aes128-ctr,aes256-ctr");
        // mac_algorithms_client_to_server
        WriteSshString(packet, "hmac-sha2-256");
        // mac_algorithms_server_to_client
        WriteSshString(packet, "hmac-sha2-256");
        // compression_algorithms_client_to_server
        WriteSshString(packet, "none");
        // compression_algorithms_server_to_client
        WriteSshString(packet, "none");
        // languages_client_to_server
        WriteSshString(packet, "");
        // languages_server_to_client
        WriteSshString(packet, "");
        // first_kex_packet_follows
        WriteByte(packet, 0);
        // reserved
        WriteUInt32(packet, 0);

        await SendSshPacketAsync(stream, packet.ToArray(), ct);
    }

    /// <summary>
    /// Simplified key exchange: send host key and new keys message.
    /// A full implementation would perform DH key exchange and derive session keys.
    /// </summary>
    private async Task PerformKeyExchangeAsync(NetworkStream stream, RSA hostKey, CancellationToken ct)
    {
        // Send SSH_MSG_KEXDH_REPLY (type 31) with host key
        using var reply = new MemoryStream();
        WriteByte(reply, 31);
        // host key blob
        var hostKeyBlob = BuildRsaHostKeyBlob(hostKey);
        WriteSshBytes(reply, hostKeyBlob);
        // f value (dummy for simplified exchange)
        WriteSshBytes(reply, new byte[32]);
        // signature
        var sigData = new byte[64];
        RandomNumberGenerator.Fill(sigData);
        var signature = hostKey.SignData(sigData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        WriteSshBytes(reply, signature);
        await SendSshPacketAsync(stream, reply.ToArray(), ct);

        // Send SSH_MSG_NEWKEYS (type 21)
        await SendSshPacketAsync(stream, new byte[] { 21 }, ct);

        // Read client NEWKEYS
        var clientNewKeys = await ReadSshPacketAsync(stream, ct);
    }

    /// <summary>
    /// Handle SSH user authentication.
    /// Reads SSH_MSG_USERAUTH_REQUEST messages and validates credentials.
    /// </summary>
    private async Task<(User? user, string? error)> HandleAuthenticationAsync(
        NetworkStream stream, string remoteIp, CancellationToken ct)
    {
        stream.ReadTimeout = 30000;
        int attempts = 0;
        const int maxAttempts = 10;

        while (attempts++ < maxAttempts && !ct.IsCancellationRequested)
        {
            var packet = await ReadSshPacketAsync(stream, ct);
            if (packet == null || packet.Length == 0)
                return (null, "Connection closed");

            var msgType = packet[0];

            // SSH_MSG_SERVICE_REQUEST (type 5)
            if (msgType == 5)
            {
                // Send SSH_MSG_SERVICE_ACCEPT (type 6)
                using var accept = new MemoryStream();
                WriteByte(accept, 6);
                WriteSshString(accept, "ssh-userauth");
                await SendSshPacketAsync(stream, accept.ToArray(), ct);
                continue;
            }

            // SSH_MSG_USERAUTH_REQUEST (type 50)
            if (msgType == 50)
            {
                var reader = new SshReader(packet, 1);
                var username = reader.ReadString();
                var serviceName = reader.ReadString();
                var method = reader.ReadString();

                User? user = null;
                string? error = null;

                if (method == "none")
                {
                    (user, error) = await _authHandler.NoClientAuthAsync(username, ct);
                }
                else if (method == "password")
                {
                    reader.ReadByte(); // skip boolean (FALSE = new password)
                    var password = reader.ReadString();
                    (user, error) = await _authHandler.PasswordAuthAsync(username, password, remoteIp, ct);
                }
                else if (method == "publickey")
                {
                    reader.ReadByte(); // skip boolean (TRUE = signature follows)
                    var algorithm = reader.ReadString();
                    var keyBlob = reader.ReadBytes();
                    (user, error) = await _authHandler.PublicKeyAuthAsync(username, keyBlob, ct);
                }
                else
                {
                    error = $"Unsupported auth method: {method}";
                }

                if (user != null)
                {
                    // Send SSH_MSG_USERAUTH_SUCCESS (type 52)
                    await SendSshPacketAsync(stream, new byte[] { 52 }, ct);
                    return (user, null);
                }

                // Send SSH_MSG_USERAUTH_FAILURE (type 51)
                await SendAuthFailureAsync(stream, ct);
            }
        }

        return (null, "Maximum authentication attempts exceeded");
    }

    private async Task SendAuthFailureAsync(NetworkStream stream, CancellationToken ct)
    {
        using var failure = new MemoryStream();
        WriteByte(failure, 51); // SSH_MSG_USERAUTH_FAILURE
        WriteSshString(failure, "password,publickey"); // allowed methods
        WriteByte(failure, 0); // partial success = false
        await SendSshPacketAsync(stream, failure.ToArray(), ct);
    }

    // ── SFTP Session Handler ──────────────────────────────────────────

    /// <summary>
    /// Handle SFTP subsystem channel after authentication.
    /// Processes SFTP protocol messages (version 3).
    /// Go: server/sftp/sftp.go DriverAdapter
    /// </summary>
    private async Task HandleSftpSessionAsync(
        NetworkStream stream, User user, string remoteIp, CancellationToken ct)
    {
        stream.ReadTimeout = 300000; // 5 min for SFTP operations

        await using var scope = _scopeFactory.CreateAsyncScope();
        var fs = scope.ServiceProvider.GetRequiredService<IFileSystemService>();
        var basePath = (user.BasePath ?? "/").TrimEnd('/');
        if (string.IsNullOrEmpty(basePath)) basePath = "/";

        while (!ct.IsCancellationRequested)
        {
            var packet = await ReadSshPacketAsync(stream, ct);
            if (packet == null || packet.Length == 0) break;

            var msgType = packet[0];

            // SSH_MSG_CHANNEL_OPEN (type 90)
            if (msgType == 90)
            {
                // Accept channel
                using var confirm = new MemoryStream();
                WriteByte(confirm, 91); // SSH_MSG_CHANNEL_OPEN_CONFIRMATION
                WriteUInt32(confirm, 0); // recipient channel
                WriteUInt32(confirm, 0); // sender channel
                WriteUInt32(confirm, 0x8000); // initial window size
                WriteUInt32(confirm, 0x8000); // max packet size
                await SendSshPacketAsync(stream, confirm.ToArray(), ct);
                continue;
            }

            // SSH_MSG_CHANNEL_DATA (type 94)
            if (msgType == 94)
            {
                var reader = new SshReader(packet, 1);
                reader.ReadUInt32(); // channel
                var data = reader.ReadBytes();

                if (data.Length < 5) continue;
                var sftpMsgType = data[4];

                // Process SFTP message
                var response = await ProcessSftpMessageAsync(
                    sftpMsgType, data, fs, basePath, user, ct);

                if (response != null)
                {
                    // Wrap in SSH_MSG_CHANNEL_DATA
                    using var channelData = new MemoryStream();
                    WriteByte(channelData, 94);
                    WriteUInt32(channelData, 0); // channel
                    WriteSshBytes(channelData, response);
                    await SendSshPacketAsync(stream, channelData.ToArray(), ct);
                }
                continue;
            }

            // SSH_MSG_CHANNEL_EOF (type 96) or SSH_MSG_CHANNEL_CLOSE (type 97)
            if (msgType == 96 || msgType == 97) break;
        }
    }

    /// <summary>
    /// Process a single SFTP protocol message.
    /// SFTP v3 message types: https://datatracker.ietf.org/doc/html/draft-ietf-secsh-filexfer-02
    /// </summary>
    private async Task<byte[]?> ProcessSftpMessageAsync(
        byte msgType, byte[] data, IFileSystemService fs, string basePath, User user,
        CancellationToken ct)
    {
        var reader = new SshReader(data, 5); // skip length(4) + type(1)

        switch (msgType)
        {
            case 1: // SSH_FXP_INIT
            {
                var version = reader.ReadUInt32();
                // Send SSH_FXP_VERSION (type 2)
                using var resp = new MemoryStream();
                WriteByte(resp, 2);
                WriteUInt32(resp, 3); // SFTP version 3
                return resp.ToArray();
            }

            case 17: // SSH_FXP_REALPATH
            {
                var id = reader.ReadUInt32();
                var path = reader.ReadString();
                var resolved = NormalizePath(path);

                // Send SSH_FXP_NAME (type 104)
                using var resp = new MemoryStream();
                WriteByte(resp, 104);
                WriteUInt32(resp, id);
                WriteUInt32(resp, 1); // count = 1
                WriteSshString(resp, resolved);
                WriteAttrs(resp, isDir: true, size: 0, modTime: DateTime.UtcNow);
                return resp.ToArray();
            }

            case 11: // SSH_FXP_OPENDIR
            {
                var id = reader.ReadUInt32();
                var path = ResolvePath(reader.ReadString(), basePath);

                try
                {
                    var handle = Guid.NewGuid().ToString("N");
                    // Send SSH_FXP_HANDLE (type 102)
                    using var resp = new MemoryStream();
                    WriteByte(resp, 102);
                    WriteUInt32(resp, id);
                    WriteSshString(resp, handle);
                    return resp.ToArray();
                }
                catch
                {
                    return BuildStatusResponse(id, 2, "No such file"); // SSH_FX_NO_SUCH_FILE
                }
            }

            case 12: // SSH_FXP_READDIR
            {
                var id = reader.ReadUInt32();
                var handle = reader.ReadString();
                // For simplicity, return SSH_FX_EOF to indicate end of directory
                return BuildStatusResponse(id, 1, "End of directory"); // SSH_FX_EOF
            }

            case 18: // SSH_FXP_STAT
            case 16: // SSH_FXP_LSTAT
            {
                var id = reader.ReadUInt32();
                var path = ResolvePath(reader.ReadString(), basePath);

                try
                {
                    var obj = await fs.GetAsync(path, ct);
                    if (obj == null)
                        return BuildStatusResponse(id, 2, "No such file");

                    using var resp = new MemoryStream();
                    WriteByte(resp, 105); // SSH_FXP_ATTRS
                    WriteUInt32(resp, id);
                    WriteAttrs(resp, obj.IsDir(), obj.GetSize(), obj.GetModTime());
                    return resp.ToArray();
                }
                catch
                {
                    return BuildStatusResponse(id, 2, "No such file");
                }
            }

            case 14: // SSH_FXP_MKDIR
            {
                var id = reader.ReadUInt32();
                var path = ResolvePath(reader.ReadString(), basePath);
                try
                {
                    await fs.MakeDirAsync(path, ct);
                    return BuildStatusResponse(id, 0, "OK"); // SSH_FX_OK
                }
                catch (Exception ex)
                {
                    return BuildStatusResponse(id, 4, ex.Message); // SSH_FX_FAILURE
                }
            }

            case 15: // SSH_FXP_RMDIR
            case 13: // SSH_FXP_REMOVE
            {
                var id = reader.ReadUInt32();
                var path = ResolvePath(reader.ReadString(), basePath);
                try
                {
                    await fs.RemoveAsync(path, ct);
                    return BuildStatusResponse(id, 0, "OK");
                }
                catch (Exception ex)
                {
                    return BuildStatusResponse(id, 4, ex.Message);
                }
            }

            case 19: // SSH_FXP_RENAME
            {
                var id = reader.ReadUInt32();
                var oldPath = ResolvePath(reader.ReadString(), basePath);
                var newPath = ResolvePath(reader.ReadString(), basePath);
                try
                {
                    var newName = Path.GetFileName(newPath);
                    await fs.RenameAsync(oldPath, newName, ct);
                    return BuildStatusResponse(id, 0, "OK");
                }
                catch (Exception ex)
                {
                    return BuildStatusResponse(id, 4, ex.Message);
                }
            }

            default:
                // Unknown message type - send failure
                if (data.Length >= 9)
                {
                    var id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(5, 4));
                    return BuildStatusResponse(id, 8, "Not supported"); // SSH_FX_OP_UNSUPPORTED
                }
                return null;
        }
    }

    private static byte[] BuildStatusResponse(uint id, uint code, string message)
    {
        using var resp = new MemoryStream();
        WriteByte(resp, 101); // SSH_FXP_STATUS
        WriteUInt32(resp, id);
        WriteUInt32(resp, code);
        WriteSshString(resp, message);
        WriteSshString(resp, "en"); // language
        return resp.ToArray();
    }

    private static void WriteAttrs(Stream s, bool isDir, long size, DateTime modTime)
    {
        uint flags = 0x00000001 | 0x00000004; // SSH_FILEXFER_ATTR_SIZE | SSH_FILEXFER_ATTR_PERMISSIONS
        flags |= 0x00000008; // SSH_FILEXFER_ATTR_ACMODTIME
        WriteUInt32(s, flags);
        WriteUInt64(s, (ulong)size);
        // permissions
        var perms = isDir ? 0x41EDu : 0x81A4u; // 0755 for dir, 0644 for file
        WriteUInt32(s, perms);
        // atime, mtime
        var epoch = (uint)new DateTimeOffset(modTime).ToUnixTimeSeconds();
        WriteUInt32(s, epoch);
        WriteUInt32(s, epoch);
    }

    // ── Path helpers ──────────────────────────────────────────────────

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        return path.TrimEnd('/') == "" ? "/" : path;
    }

    private static string ResolvePath(string sftpPath, string basePath)
    {
        var clean = NormalizePath(sftpPath);
        if (clean == "/") return basePath;
        if (basePath == "/") return clean;
        return basePath + clean;
    }

    // ── SSH packet I/O ────────────────────────────────────────────────

    private async Task SendSshPacketAsync(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        // Simplified: no encryption, just length + payload
        var lenBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        await stream.WriteAsync(lenBytes, ct);
        await stream.WriteAsync(data, ct);
    }

    private async Task<byte[]?> ReadSshPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await stream.ReadAsync(lenBuf.AsMemory(read, 4 - read), ct);
            if (n == 0) return null;
            read += n;
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
        if (length > 1024 * 1024) return null; // sanity check

        var data = new byte[length];
        read = 0;
        while (read < (int)length)
        {
            var n = await stream.ReadAsync(data.AsMemory(read, (int)length - read), ct);
            if (n == 0) return null;
            read += n;
        }
        return data;
    }

    // ── SSH binary helpers ────────────────────────────────────────────

    private static byte[] BuildRsaHostKeyBlob(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        using var ms = new MemoryStream();
        WriteSshString(ms, "ssh-rsa"u8);
        WriteSshBigInt(ms, parameters.Exponent!);
        WriteSshBigInt(ms, parameters.Modulus!);
        return ms.ToArray();
    }

    private static void WriteSshString(Stream s, string value)
        => WriteSshBytes(s, Encoding.UTF8.GetBytes(value));

    private static void WriteSshString(Stream s, ReadOnlySpan<byte> value)
    {
        WriteUInt32(s, (uint)value.Length);
        s.Write(value);
    }

    private static void WriteSshBytes(Stream s, byte[] value)
    {
        WriteUInt32(s, (uint)value.Length);
        s.Write(value);
    }

    private static void WriteSshBigInt(Stream s, byte[] value)
    {
        if (value.Length > 0 && (value[0] & 0x80) != 0)
        {
            WriteUInt32(s, (uint)(value.Length + 1));
            s.WriteByte(0);
        }
        else
        {
            WriteUInt32(s, (uint)value.Length);
        }
        s.Write(value);
    }

    private static void WriteByte(Stream s, byte v) => s.WriteByte(v);
    private static void WriteUInt32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }
    private static void WriteUInt64(Stream s, ulong v)
    {
        WriteUInt32(s, (uint)(v >> 32));
        WriteUInt32(s, (uint)v);
    }

    /// <summary>Parse Go-style listen address like ":5222".</summary>
    internal static (string address, int port) ParseListenAddress(string listen)
    {
        if (string.IsNullOrEmpty(listen)) return ("", 22);
        listen = listen.Trim();
        if (listen.StartsWith(':') && int.TryParse(listen[1..], out var port))
            return ("", port);
        var lastColon = listen.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(listen[(lastColon + 1)..], out var p2))
            return (listen[..lastColon], p2);
        if (int.TryParse(listen, out var p3))
            return ("", p3);
        return ("", 22);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Stop();
    }
}

/// <summary>Helper for reading SSH binary format data.</summary>
internal ref struct SshReader
{
    private readonly byte[] _data;
    private int _pos;

    public SshReader(byte[] data, int startPos)
    {
        _data = data;
        _pos = startPos;
    }

    public byte ReadByte()
    {
        if (_pos >= _data.Length) return 0;
        return _data[_pos++];
    }

    public uint ReadUInt32()
    {
        if (_pos + 4 > _data.Length) return 0;
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    public string ReadString()
    {
        var len = (int)ReadUInt32();
        if (len <= 0 || _pos + len > _data.Length) return "";
        var s = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len;
        return s;
    }

    public byte[] ReadBytes()
    {
        var len = (int)ReadUInt32();
        if (len <= 0 || _pos + len > _data.Length) return [];
        var b = new byte[len];
        Array.Copy(_data, _pos, b, 0, len);
        _pos += len;
        return b;
    }
}
