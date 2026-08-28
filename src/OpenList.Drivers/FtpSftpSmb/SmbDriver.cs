using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// SMB 存储驱动（基础存根）。
/// 完整实现需集成 SMBLibrary 客户端，因该库 API 较底层而颇具复杂度。此存根提供
/// 配置字段元数据，使驱动可被发现，但所有文件系统操作在完整实现前将抛出
/// NotImplementedException。
/// </summary>
public sealed class SmbDriver : ZioDriverBase
{
    /// <summary>驱动名称。</summary>
    public override string DriverName => "SMB";
    /// <summary>未实现写入，当前按只读对待。</summary>
    public override bool ReadOnly => true;
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
    /// 初始化 SMB 驱动（存根）：当前仅校验必填字段，并以只读内存文件系统占位挂载。
    /// </summary>
    /// <param name="config">包含 host、share 等字段的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示初始化完成的任务。</returns>
    /// <exception cref="InvalidOperationException">未配置 <c>host</c> 或 <c>share</c> 时抛出。</exception>
    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<SmbAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Host))
            throw new InvalidOperationException("host is required");
        if (string.IsNullOrWhiteSpace(Addition.Share))
            throw new InvalidOperationException("share is required");

        // TODO: 使用 SMBLibrary.Client.SMB2Client 实现 SMB 文件系统。
        // 现阶段先用只读内存文件系统占位，使驱动能正常加载而不引发崩溃。
        System = new MemoryFileSystem();
        return Task.CompletedTask;
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
