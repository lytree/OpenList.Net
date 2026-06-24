using System.Net;
using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using WebDav;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.WebDAV;

/// <summary>
/// WebDAV storage driver using WebDav.Client.
/// </summary>
public sealed class WebDavDriver : ZioDriverBase, IFileSystemUploader, IFileSystemLinkProvider
{
    public override string DriverName => "WebDAV";
    public override bool ReadOnly => false;
    public override bool IsCloudDriver => false;

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

    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<WebDavAddition>();
        if (string.IsNullOrWhiteSpace(Addition.Endpoint))
            throw new InvalidOperationException("endpoint is required");

        var paramsObj = new WebDavClientParams
        {
            BaseAddress = new Uri(Addition.Endpoint),
        };

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

    // --- IFileSystemLinkProvider ---

    public Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default)
    {
        var rootPath = Addition.RootFolderPath?.Trim('/') ?? "";
        var relative = path.TrimStart('/');
        var full = string.IsNullOrEmpty(rootPath) ? relative : rootPath + "/" + relative;
        var url = Addition.Endpoint.TrimEnd('/') + "/" + full;
        return Task.FromResult<Uri?>(new Uri(url));
    }
}

internal sealed class WebDavAddition
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";

    [JsonPropertyName("root_folder_path")]
    public string? RootFolderPath { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("use_basic_auth")]
    public bool UseBasicAuth { get; set; } = true;
}
