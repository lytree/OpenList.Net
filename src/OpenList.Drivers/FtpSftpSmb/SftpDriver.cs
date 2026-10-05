using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Renci.SshNet;
using OpenList.Vfs;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// 基于 SSH.NET 的 SFTP 存储驱动。
/// </summary>
public sealed class SftpDriver : DriverBase, IFileSystemUploader
{
    /// <summary>驱动名称。</summary>
    public override string DriverName => "SFTP";
    /// <summary>SFTP 支持写入，非只读。</summary>
    public override bool ReadOnly => false;
    /// <summary>SFTP 不属于云存储驱动器。</summary>
    public override bool IsCloudDriver => false;

    /// <summary>该驱动暴露给管理端配置的字段枚举。</summary>
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

    /// <summary>SFTP 驱动专用配置项（通过 <see cref="InitAsync"/> 解析填充）。</summary>
    private SftpAddition Addition { get; set; } = new();

    /// <summary>
    /// 初始化 SFTP 驱动：校验字段、按密码或私钥构造 SSH.NET 客户端并挂载 SFTP 文件系统。
    /// </summary>
    /// <param name="config">包含 host、凭据、私钥等字段的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示初始化完成的任务。</returns>
    /// <exception cref="InvalidOperationException">未配置 <c>host</c> 或 <c>username</c> 时抛出。</exception>
    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<SftpAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Host))
            throw new InvalidOperationException("host is required");
        if (string.IsNullOrWhiteSpace(Addition.Username))
            throw new InvalidOperationException("username is required");

        SftpClient client;
        // 优先使用私钥认证，否则回退为密码认证。
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

    /// <summary>上传普通文件：流式写入目标路径。</summary>
    public async Task PutAsync(string path, Stream stream, CancellationToken ct = default)
    {
        var upath = UPath.Root / path.TrimStart('/');
        using var dst = System!.OpenFile(upath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(dst, ct);
    }

    /// <summary>表单上传：目录路径拼接文件名后复用 <see cref="PutAsync"/>。</summary>
    public async Task PutFormAsync(string path, Stream stream, string fileName, CancellationToken ct = default)
    {
        await PutAsync(path + "/" + fileName, stream, ct);
    }
}

/// <summary>SFTP 驱动的配置项模型，映射 host、凭据、私钥等 JSON 输入字段。</summary>
internal sealed class SftpAddition
{
    /// <summary>SFTP 服务器主机地址。</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>SFTP 服务器端口。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    /// <summary>登录用户名。</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>登录密码（与私钥二选一）。</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>私钥文件路径（与密码二选一）。</summary>
    [JsonPropertyName("private_key")]
    public string? PrivateKey { get; set; }

    /// <summary>加密私钥的口令。</summary>
    [JsonPropertyName("private_key_passphrase")]
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>服务器上的根路径。</summary>
    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }
}
