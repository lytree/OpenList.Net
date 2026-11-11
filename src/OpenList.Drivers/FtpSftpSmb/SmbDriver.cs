using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using SMBLibrary;
using SMBLibrary.Client;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// 基于 SMBLibrary 客户端（SMB2）的 SMB 存储驱动。
/// </summary>
public sealed class SmbDriver : DriverBase
{
    /// <summary>驱动名称。</summary>
    public override string DriverName => "SMB";
    /// <summary>SMB 驱动支持写入。</summary>
    public override bool ReadOnly => false;
    /// <summary>SMB 不属于云存储驱动器。</summary>
    public override bool IsCloudDriver => false;

    /// <summary>该驱动暴露给管理端配置的字段枚举。</summary>
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

    /// <summary>SMB 驱动专用配置项（通过 <see cref="InitAsync"/> 解析填充）。</summary>
    private SmbAddition Addition { get; set; } = new();

    /// <summary>
    /// 初始化 SMB 驱动：连接服务器、登录、建立共享树连接并挂载 SMB 文件系统。
    /// </summary>
    /// <param name="config">包含 host、share 等字段的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示初始化完成的任务。</returns>
    /// <exception cref="InvalidOperationException">必填字段缺失、端口非 445 或连接/登录失败时抛出。</exception>
    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<SmbAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Host))
            throw new InvalidOperationException("host is required");
        if (string.IsNullOrWhiteSpace(Addition.Share))
            throw new InvalidOperationException("share is required");
        // SMBLibrary 的 SMB2Client 固定连接 445 端口，无法指定自定义端口。
        if (Addition.Port is not (0 or 445))
            throw new InvalidOperationException("only the default SMB port 445 is supported");

        var client = new SMB2Client();
        try
        {
            if (!client.Connect(Addition.Host, SMBTransportType.DirectTCPTransport))
                throw new InvalidOperationException($"failed to connect to SMB server {Addition.Host}:445");

            var login = client.Login(Addition.Domain ?? "", Addition.Username ?? "", Addition.Password ?? "");
            if (login != NTStatus.STATUS_SUCCESS)
                throw new InvalidOperationException($"SMB login failed: {login}");

            var store = client.TreeConnect(Addition.Share, out var treeStatus);
            if (treeStatus != NTStatus.STATUS_SUCCESS)
                throw new InvalidOperationException($"failed to connect to SMB share '{Addition.Share}': {treeStatus}");

            System = new SmbFileSystem(client, store, Addition.RootFolderPath);
            return Task.CompletedTask;
        }
        catch
        {
            client.Disconnect();
            throw;
        }
    }
}

/// <summary>SMB 驱动的配置项模型，映射 host、share 等 JSON 输入字段。</summary>
internal sealed class SmbAddition
{
    /// <summary>SMB 服务器主机地址。</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>SMB 服务器端口。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 445;

    /// <summary>登录用户名。</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>登录密码。</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>共享名。</summary>
    [JsonPropertyName("share")]
    public string Share { get; set; } = "";

    /// <summary>可选的域。</summary>
    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    /// <summary>共享内的根路径。</summary>
    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }
}
