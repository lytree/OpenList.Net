using System.Reflection;
using System.Text.Json.Serialization;
using Minio;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.S3;

/// <summary>
/// 兼容 S3 协议的对象存储驱动（AWS S3、Minio、R2、B2 等）。
/// 使用 Minio SDK，可与任意 S3 兼容 API 对接。对应 OpenList Go 的 <c>drivers/s3</c>。
/// </summary>
public sealed class S3Driver : ZioDriverBase, IFileSystemUploader, IFileSystemLinkProvider
{
    /// <summary>驱动名称。</summary>
    public override string DriverName => "S3";
    /// <summary>S3 支持写入，非只读。</summary>
    public override bool ReadOnly => false;
    /// <summary>S3 属于对象存储，但按本地语义封装（此处不视为云驱动）。</summary>
    public override bool IsCloudDriver => false;

    /// <summary>该驱动暴露给管理端配置的字段枚举。</summary>
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

    /// <summary>S3 驱动专用配置项（通过 <see cref="InitAsync"/> 解析填充）。</summary>
    public S3Addition Addition { get; private set; } = new();

    /// <summary>
    /// 初始化 S3 驱动：校验必填字段、构造 Minio 客户端并挂载 S3 文件系统。
    /// </summary>
    /// <param name="config">包含 endpoint、密钥、bucket 等字段的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示初始化完成的任务。</returns>
    /// <exception cref="InvalidOperationException">endpoint / access_key_id / secret_access_key / bucket 缺失时抛出。</exception>
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
        // Minio 首版 API 未暴露公开设置器，故通过反射设置连接配置项。
        var configType = minioConfig.GetType();
        configType.GetProperty("Endpoint")!.SetValue(minioConfig, Addition.Endpoint);
        configType.GetProperty("AccessKey")!.SetValue(minioConfig, Addition.AccessKeyId);
        configType.GetProperty("SecretKey")!.SetValue(minioConfig, Addition.SecretAccessKey);
        configType.GetProperty("Region")!.SetValue(minioConfig, string.IsNullOrWhiteSpace(Addition.Region) ? "us-east-1" : Addition.Region);
        configType.GetProperty("Secure")!.SetValue(minioConfig, Addition.UseSSL);
        if (!string.IsNullOrWhiteSpace(Addition.SessionToken))
            configType.GetProperty("SessionToken")!.SetValue(minioConfig, Addition.SessionToken);

        // root_folder_path 作为 bucket 内的公共前缀，整个桶作为文件系统挂载。
        var rootPrefix = Addition.RootFolderPath?.TrimStart('/') ?? "";
        System = new S3FileSystem(client, Addition.Bucket, rootPrefix);
        return Task.CompletedTask;
    }

    // --- IFileSystemUploader ---

    /// <summary>上传普通文件：流式写入目标路径。</summary>
    public async Task PutAsync(string path, Stream stream, CancellationToken ct = default)
    {
        var upath = UPath.Root / path.TrimStart('/');
        using var dest = System!.OpenFile(upath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(dest, ct);
    }

    /// <summary>表单上传：对 S3 而言路径已是完整对象键，直接复用 <see cref="PutAsync"/>。</summary>
    public async Task PutFormAsync(string path, Stream stream, string fileName, CancellationToken ct = default)
    {
        await PutAsync(path, stream, ct);
    }

    // --- IFileSystemLinkProvider ---

    /// <summary>
    /// 生成对象的公开链接（当前未实现，返回 null 表示不支持）。
    /// </summary>
    public Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default)
    {
        // 可在后续版本在此生成 S3 预签名 URL。
        // 当前返回 null 表明暂不支持链接生成。
        return Task.FromResult<Uri?>(null);
    }
}

/// <summary>S3 驱动的配置项模型，映射 endpoint、密钥、bucket 等 JSON 输入字段。</summary>
public sealed class S3Addition
{
    /// <summary>S3 兼容服务的端点 URL。</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>AWS 区域。</summary>
    [JsonPropertyName("region")]
    public string Region { get; set; } = "us-east-1";

    /// <summary>访问密钥 ID。</summary>
    [JsonPropertyName("access_key_id")]
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>访问密钥（秘密部分）。</summary>
    [JsonPropertyName("secret_access_key")]
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>存储桶名称。</summary>
    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = string.Empty;

    /// <summary>桶内的根前缀（例如 /photos）。</summary>
    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }

    /// <summary>是否启用 HTTPS。</summary>
    [JsonPropertyName("use_ssl")]
    public bool UseSSL { get; set; } = true;

    /// <summary>可选的临时会话令牌。</summary>
    [JsonPropertyName("session_token")]
    public string? SessionToken { get; set; }
}
