using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Renci.SshNet;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// SFTP storage driver using SSH.NET.
/// </summary>
public sealed class SftpDriver : ZioDriverBase, IFileSystemUploader
{
    public override string DriverName => "SFTP";
    public override bool ReadOnly => false;
    public override bool IsCloudDriver => false;

    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new() { Name = "host", Help = "SFTP server host", Required = true, ValueType = typeof(string) },
        new() { Name = "port", Help = "SFTP server port", ValueType = typeof(int), Default = 22 },
        new() { Name = "username", Help = "Username", Required = true, ValueType = typeof(string) },
        new() { Name = "password", Help = "Password (use password OR private_key)", ValueType = typeof(string) },
        new() { Name = "private_key", Help = "Private key file path (use password OR private_key)", ValueType = typeof(string) },
        new() { Name = "private_key_passphrase", Help = "Passphrase for encrypted private key", ValueType = typeof(string) },
        new() { Name = "root_folder_path", Help = "Root path on the server", ValueType = typeof(string) },
    ];

    private SftpAddition Addition { get; set; } = new();

    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<SftpAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Host))
            throw new InvalidOperationException("host is required");
        if (string.IsNullOrWhiteSpace(Addition.Username))
            throw new InvalidOperationException("username is required");

        SftpClient client;
        if (!string.IsNullOrWhiteSpace(Addition.PrivateKey))
        {
            var keyFile = string.IsNullOrWhiteSpace(Addition.PrivateKeyPassphrase)
                ? new PrivateKeyFile(Addition.PrivateKey)
                : new PrivateKeyFile(Addition.PrivateKey, Addition.PrivateKeyPassphrase);
            client = new SftpClient(Addition.Host, Addition.Port, Addition.Username, keyFile);
        }
        else
        {
            client = new SftpClient(Addition.Host, Addition.Port, Addition.Username, Addition.Password ?? "");
        }

        var rootPath = Addition.RootFolderPath?.TrimStart('/') ?? "";
        System = new SftpFileSystem(client, "/" + rootPath);
        return Task.CompletedTask;
    }

    // --- IFileSystemUploader ---

    public async Task PutAsync(string path, Stream stream, CancellationToken ct = default)
    {
        var upath = UPath.Root / path.TrimStart('/');
        using var dst = System!.OpenFile(upath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(dst, ct);
    }

    public async Task PutFormAsync(string path, Stream stream, string fileName, CancellationToken ct = default)
    {
        await PutAsync(path + "/" + fileName, stream, ct);
    }
}

internal sealed class SftpAddition
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("private_key")]
    public string? PrivateKey { get; set; }

    [JsonPropertyName("private_key_passphrase")]
    public string? PrivateKeyPassphrase { get; set; }

    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }
}
