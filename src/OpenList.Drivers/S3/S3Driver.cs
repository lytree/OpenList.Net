using System.Reflection;
using System.Text.Json.Serialization;
using Minio;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.S3;

/// <summary>
/// S3-compatible storage driver (AWS S3, Minio, R2, B2, etc.).
/// Uses the Minio SDK which works with any S3-compatible API.
/// Mirrors OpenList Go <c>drivers/s3</c>.
/// </summary>
public sealed class S3Driver : ZioDriverBase, IFileSystemUploader, IFileSystemLinkProvider
{
    public override string DriverName => "S3";
    public override bool ReadOnly => false;
    public override bool IsCloudDriver => false;

    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new() { Name = "endpoint", Help = "S3 endpoint URL (e.g. https://s3.amazonaws.com)", Required = true, ValueType = typeof(string) },
        new() { Name = "region", Help = "AWS region", ValueType = typeof(string), Default = "us-east-1" },
        new() { Name = "access_key_id", Help = "Access key ID", Required = true, ValueType = typeof(string) },
        new() { Name = "secret_access_key", Help = "Secret access key", Required = true, ValueType = typeof(string) },
        new() { Name = "bucket", Help = "Bucket name", Required = true, ValueType = typeof(string) },
        new() { Name = "root_folder_path", Help = "Root path within bucket (e.g. /photos)", ValueType = typeof(string) },
        new() { Name = "use_ssl", Help = "Use HTTPS", ValueType = typeof(bool), Default = true },
        new() { Name = "session_token", Help = "Temporary session token (optional)", ValueType = typeof(string) },
    ];

    public S3Addition Addition { get; private set; } = new();

    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<S3Addition>();
        if (string.IsNullOrWhiteSpace(Addition.Endpoint))
            throw new InvalidOperationException("endpoint is required");
        if (string.IsNullOrWhiteSpace(Addition.AccessKeyId))
            throw new InvalidOperationException("access_key_id is required");
        if (string.IsNullOrWhiteSpace(Addition.SecretAccessKey))
            throw new InvalidOperationException("secret_access_key is required");
        if (string.IsNullOrWhiteSpace(Addition.Bucket))
            throw new InvalidOperationException("bucket is required");

        var client = new MinioClient();
        var minioConfig = client.Config;
        var configType = minioConfig.GetType();
        configType.GetProperty("Endpoint")!.SetValue(minioConfig, Addition.Endpoint);
        configType.GetProperty("AccessKey")!.SetValue(minioConfig, Addition.AccessKeyId);
        configType.GetProperty("SecretKey")!.SetValue(minioConfig, Addition.SecretAccessKey);
        configType.GetProperty("Region")!.SetValue(minioConfig, string.IsNullOrWhiteSpace(Addition.Region) ? "us-east-1" : Addition.Region);
        configType.GetProperty("Secure")!.SetValue(minioConfig, Addition.UseSSL);
        if (!string.IsNullOrWhiteSpace(Addition.SessionToken))
            configType.GetProperty("SessionToken")!.SetValue(minioConfig, Addition.SessionToken);

        var rootPrefix = Addition.RootFolderPath?.TrimStart('/') ?? "";
        System = new S3FileSystem(client, Addition.Bucket, rootPrefix);
        return Task.CompletedTask;
    }

    // --- IFileSystemUploader ---

    public async Task PutAsync(string path, Stream stream, CancellationToken ct = default)
    {
        var upath = UPath.Root / path.TrimStart('/');
        using var dest = System!.OpenFile(upath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(dest, ct);
    }

    public async Task PutFormAsync(string path, Stream stream, string fileName, CancellationToken ct = default)
    {
        await PutAsync(path, stream, ct);
    }

    // --- IFileSystemLinkProvider ---

    public Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default)
    {
        // S3 presigned URLs could be generated here.
        // For now, return null to indicate not supported.
        return Task.FromResult<Uri?>(null);
    }
}

public sealed class S3Addition
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = "us-east-1";

    [JsonPropertyName("access_key_id")]
    public string AccessKeyId { get; set; } = string.Empty;

    [JsonPropertyName("secret_access_key")]
    public string SecretAccessKey { get; set; } = string.Empty;

    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = string.Empty;

    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }

    [JsonPropertyName("use_ssl")]
    public bool UseSSL { get; set; } = true;

    [JsonPropertyName("session_token")]
    public string? SessionToken { get; set; }
}
