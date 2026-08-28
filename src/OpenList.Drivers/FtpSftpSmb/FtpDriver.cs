using System.Net;
using System.Text.Json.Serialization;
using FluentFTP;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// 基于 FluentFTP 的 FTP 存储驱动。
/// </summary>
public sealed class FtpDriver : ZioDriverBase, IFileSystemUploader
{
    /// <summary>驱动名称。</summary>
    public override string DriverName => "FTP";
    /// <summary>FTP 支持写入，非只读。</summary>
    public override bool ReadOnly => false;
    /// <summary>FTP 不属于云存储驱动器。</summary>
    public override bool IsCloudDriver => false;

    /// <summary>该驱动暴露给管理端配置的字段枚举。</summary>
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

    /// <summary>FTP 驱动专用配置项（通过 <see cref="InitAsync"/> 解析填充）。</summary>
    private FtpAddition Addition { get; set; } = new();

    /// <summary>
    /// 初始化 FTP 驱动：校验主机、构造 FluentFTP 客户端（可选显式 TLS）并挂载 FTP 文件系统。
    /// </summary>
    /// <param name="config">包含 host、凭据等字段的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示初始化完成的任务。</returns>
    /// <exception cref="InvalidOperationException">未配置 <c>host</c> 时抛出。</exception>
    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<FtpAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Host))
            throw new InvalidOperationException("host is required");

        // 未指定用户名时使用匿名登录。
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

/// <summary>FTP 驱动的配置项模型，映射 host、凭据等 JSON 输入字段。</summary>
internal sealed class FtpAddition
{
    /// <summary>FTP 服务器主机地址。</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>FTP 服务器端口。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 21;

    /// <summary>登录用户名。</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>登录密码。</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>服务器上的根路径。</summary>
    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }

    /// <summary>是否使用 FTPS（显式 TLS 加密）。</summary>
    [JsonPropertyName("use_tls")]
    public bool UseTLS { get; set; } = false;
}
