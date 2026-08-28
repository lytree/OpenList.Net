using System.Net;
using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using WebDav;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.WebDAV;

/// <summary>
/// 基于 WebDav.Client 的 WebDAV 存储驱动。
/// </summary>
public sealed class WebDavDriver : ZioDriverBase, IFileSystemUploader, IFileSystemLinkProvider
{
    /// <summary>驱动名称。</summary>
    public override string DriverName => "WebDAV";
    /// <summary>WebDAV 支持写入，非只读。</summary>
    public override bool ReadOnly => false;
    /// <summary>WebDAV 不属于云存储驱动器。</summary>
    public override bool IsCloudDriver => false;

    /// <summary>该驱动暴露给管理端配置的字段枚举。</summary>
    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new() { Name = "endpoint", Help = "WebDAV server URL (e.g. https://dav.example.com)", Required = true, ValueType = typeof(string) },
        new() { Name = "root_folder_path", Help = "Root path on the server", ValueType = typeof(string) },
        new() { Name = "username", Help = "Username for authentication", ValueType = typeof(string) },
        new() { Name = "password", Help = "Password for authentication", ValueType = typeof(string) },
        new() { Name = "use_basic_auth", Help = "Use Basic authentication", ValueType = typeof(bool), Default = true },
    ];

    private WebDavAddition Addition { get; set; } = new();

    /// <summary>
    /// 初始化 WebDAV 驱动：校验端点、构造客户端（可选 Basic 认证）并挂载 WebDAV 文件系统。
    /// </summary>
    /// <param name="config">包含 endpoint、凭据等字段的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示初始化完成的任务。</returns>
    /// <exception cref="InvalidOperationException">未配置 <c>endpoint</c> 时抛出。</exception>
    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<WebDavAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Endpoint))
            throw new InvalidOperationException("endpoint is required");

        var paramsObj = new WebDavClientParams
        {
            BaseAddress = new Uri(Addition.Endpoint),
        };

        // 仅当配置了用户名时才设置 Basic 认证凭据。
        if (!string.IsNullOrWhiteSpace(Addition.Username))
        {
            paramsObj.Credentials = new NetworkCredential(Addition.Username, Addition.Password ?? "");
            paramsObj.UseDefaultCredentials = false;
        }

        var client = new WebDavClient(paramsObj);
        var rootPath = Addition.RootFolderPath?.Trim('/') ?? "";
        System = new WebDavFileSystem(client, Addition.Endpoint, rootPath);
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

    // --- IFileSystemLinkProvider ---

    /// <summary>
    /// 生成 WebDAV 资源的公开 URL（将路径映射到端点下的完整地址）。
    /// </summary>
    /// <param name="path">Zio 相对路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>拼接好的 URL。</returns>
    public Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default)
    {
        var rootPath = Addition.RootFolderPath?.Trim('/') ?? "";
        var relative = path.TrimStart('/');
        var full = string.IsNullOrEmpty(rootPath) ? relative : rootPath + "/" + relative;
        var url = Addition.Endpoint.TrimEnd('/') + "/" + full;
        return Task.FromResult<Uri?>(new Uri(url));
    }
}

/// <summary>WebDAV 驱动的配置项模型，映射端点、凭据等 JSON 输入字段。</summary>
internal sealed class WebDavAddition
{
    /// <summary>WebDAV 服务器端点 URL。</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";

    /// <summary>服务器上的根路径。</summary>
    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }

    /// <summary>用于认证的用户名。</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>用于认证的密码。</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>是否启用 Basic 认证。</summary>
    [JsonPropertyName("use_basic_auth")]
    public bool UseBasicAuth { get; set; } = true;
}
