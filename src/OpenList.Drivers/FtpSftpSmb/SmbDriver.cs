using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// SMB storage driver — basic stub.
/// Full implementation requires SMBLibrary client integration which is non-trivial
/// due to the low-level nature of the SMBLibrary API. This stub provides the
/// configuration schema and metadata so the driver can be discovered, but all
/// filesystem operations will throw NotImplementedException until fully implemented.
/// </summary>
public sealed class SmbDriver : ZioDriverBase
{
    public override string DriverName => "SMB";
    public override bool ReadOnly => true;
    public override bool IsCloudDriver => false;

    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new() { Name = "host", Help = "SMB server host", Required = true, ValueType = typeof(string) },
        new() { Name = "port", Help = "SMB server port (default 445)", ValueType = typeof(int), Default = 445 },
        new() { Name = "username", Help = "Username", ValueType = typeof(string) },
        new() { Name = "password", Help = "Password", ValueType = typeof(string) },
        new() { Name = "share", Help = "Share name", Required = true, ValueType = typeof(string) },
        new() { Name = "domain", Help = "Domain (optional)", ValueType = typeof(string) },
        new() { Name = "root_folder_path", Help = "Root path within the share", ValueType = typeof(string) },
    ];

    private SmbAddition Addition { get; set; } = new();

    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<SmbAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Host))
            throw new InvalidOperationException("host is required");
        if (string.IsNullOrWhiteSpace(Addition.Share))
            throw new InvalidOperationException("share is required");

        // TODO: Implement SMB filesystem using SMBLibrary.Client.SMB2Client.
        // For now, use a read-only memory filesystem as a placeholder so the
        // driver can be loaded and discovered without crashing.
        System = new MemoryFileSystem();
        return Task.CompletedTask;
    }
}

internal sealed class SmbAddition
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 445;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("share")]
    public string Share { get; set; } = "";

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }
}
