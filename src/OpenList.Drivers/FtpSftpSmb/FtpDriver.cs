using System.Net;
using System.Text.Json.Serialization;
using FluentFTP;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// FTP storage driver using FluentFTP.
/// </summary>
public sealed class FtpDriver : ZioDriverBase, IFileSystemUploader
{
    public override string DriverName => "FTP";
    public override bool ReadOnly => false;
    public override bool IsCloudDriver => false;

    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new() { Name = "host", Help = "FTP server host", Required = true, ValueType = typeof(string) },
        new() { Name = "port", Help = "FTP server port", ValueType = typeof(int), Default = 21 },
        new() { Name = "username", Help = "Username", ValueType = typeof(string) },
        new() { Name = "password", Help = "Password", ValueType = typeof(string) },
        new() { Name = "root_folder_path", Help = "Root path on the server", ValueType = typeof(string) },
        new() { Name = "use_tls", Help = "Use FTPS (TLS encryption)", ValueType = typeof(bool), Default = false },
    ];

    private FtpAddition Addition { get; set; } = new();

    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<FtpAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Host))
            throw new InvalidOperationException("host is required");

        var client = new AsyncFtpClient(Addition.Host, Addition.Username ?? "anonymous", Addition.Password ?? "", Addition.Port);
        if (Addition.UseTLS)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        }

        var rootPath = Addition.RootFolderPath?.TrimStart('/') ?? "";
        System = new FtpFileSystem(client, "/" + rootPath);
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

internal sealed class FtpAddition
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 21;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }

    [JsonPropertyName("use_tls")]
    public bool UseTLS { get; set; } = false;
}
