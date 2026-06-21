using System.Text.Json;
using WebDav;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;

namespace OpenList.Drivers.WebDAV;

public class WebDavDriver : ZioDriverBase, IFileSystemLinkProvider, IFileSystemUploader
{
    private WebDavClient? _client;

    public override DriverConfig Config { get; } = new()
    {
        Name = "WebDAV",
        DefaultRoot = "/",
        CheckStatus = true
    };

    private WebDavAddition? _addition;
    public override object Addition => _addition ??= JsonSerializer.Deserialize<WebDavAddition>(_storage?.Addition ?? "{}") ?? new WebDavAddition();

    public override DriverInfo GetDriverInfo()
    {
        var additionalItems = BuildItemsFromObject(new WebDavAddition());
        var commonItems = new List<DriverItem>
        {
            new() { Name = "mount_path", Type = "string", Required = true, Help = "The path you want to mount to, it is unique and cannot be repeated" },
            new() { Name = "order", Type = "number", Help = "use to sort" },
            new() { Name = "remark", Type = "text" },
            new() { Name = "webdav_policy", Type = "select", Options = "use_proxy_url,native_proxy", Default = "native_proxy", Required = true },
            new() { Name = "down_proxy_url", Type = "text" },
            new() { Name = "disable_proxy_sign", Type = "bool", Default = "false" },
            new() { Name = "order_by", Type = "select", Options = "name,size,modified" },
            new() { Name = "order_direction", Type = "select", Options = "asc,desc" },
            new() { Name = "extract_folder", Type = "select", Options = "front,back" },
            new() { Name = "disable_index", Type = "bool", Default = "false", Required = true },
            new() { Name = "enable_sign", Type = "bool", Default = "false", Required = true },
        };
        return new DriverInfo { Common = commonItems, Additional = additionalItems, Config = Config };
    }

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (WebDavAddition)Addition;

        var clientParams = new WebDavClientParams
        {
            BaseAddress = new Uri(addition.WebdavURL),
            Credentials = string.IsNullOrEmpty(addition.Username) ? null
                : new System.Net.NetworkCredential(addition.Username, addition.Password)
        };

        _client = new WebDavClient(clientParams);
        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default)
    {
        _client?.Dispose();
        _client = null;
        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (_client == null)
        {
            InitAsync().GetAwaiter().GetResult();
        }
    }

    #region IFileSystem Impl

    protected override void CreateDirectoryImpl(UPath path)
    {
        EnsureConnected();
        _client!.Mkcol(path.FullName + "/").GetAwaiter().GetResult();
    }

    protected override bool DirectoryExistsImpl(UPath path)
    {
        EnsureConnected();
        var response = _client!.Propfind(path.FullName).GetAwaiter().GetResult();
        if (!response.IsSuccessful) return false;
        return response.Resources.Any(r => r.IsCollection && r.Uri.TrimEnd('/') == path.FullName.TrimEnd('/'));
    }

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        EnsureConnected();
        _client!.Move(srcPath.FullName, destPath.FullName + "/", new MoveParameters { Overwrite = true }).GetAwaiter().GetResult();
    }

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        EnsureConnected();
        _client!.Delete(path.FullName).GetAwaiter().GetResult();
    }

    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        EnsureConnected();
        _client!.Copy(srcPath.FullName, destPath.FullName, new CopyParameters { Overwrite = overwrite }).GetAwaiter().GetResult();
    }

    protected override long GetFileLengthImpl(UPath path)
    {
        EnsureConnected();
        var response = _client!.Propfind(path.FullName).GetAwaiter().GetResult();
        if (!response.IsSuccessful) return 0;
        var resource = response.Resources.FirstOrDefault(r => !r.IsCollection);
        return resource?.ContentLength ?? 0;
    }

    protected override bool FileExistsImpl(UPath path)
    {
        EnsureConnected();
        var response = _client!.Propfind(path.FullName).GetAwaiter().GetResult();
        if (!response.IsSuccessful) return false;
        return response.Resources.Any(r => !r.IsCollection && r.Uri.TrimEnd('/') == path.FullName.TrimEnd('/'));
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        EnsureConnected();
        _client!.Move(srcPath.FullName, destPath.FullName, new MoveParameters { Overwrite = true }).GetAwaiter().GetResult();
    }

    protected override void DeleteFileImpl(UPath path)
    {
        EnsureConnected();
        _client!.Delete(path.FullName).GetAwaiter().GetResult();
    }

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        EnsureConnected();
        if (access.HasFlag(FileAccess.Read))
        {
            var response = _client!.GetRawFile(path.FullName).GetAwaiter().GetResult();
            return response.Stream;
        }
        // For write: return a stream that uploads on dispose
        return new WebDavWriteStream(_client!, path.FullName);
    }

    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        EnsureConnected();
        var response = _client!.Propfind(path.FullName).GetAwaiter().GetResult();
        if (!response.IsSuccessful) return DateTime.MinValue;
        var resource = response.Resources.FirstOrDefault();
        return resource?.LastModifiedDate?.ToUniversalTime() ?? DateTime.MinValue;
    }

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        EnsureConnected();
        var response = _client!.Propfind(path.FullName, new PropfindParameters { ApplyTo = ApplyTo.Propfind.ResourceAndChildren }).GetAwaiter().GetResult();
        if (!response.IsSuccessful) yield break;

        foreach (var resource in response.Resources)
        {
            var name = Uri.UnescapeDataString(resource.Uri.TrimEnd('/').Split('/').Last());
            if (string.IsNullOrEmpty(name) || name == path.GetName()) continue;

            var itemPath = path / name;
            var isDir = resource.IsCollection;

            if (searchTarget == SearchTarget.File && isDir) continue;
            if (searchTarget == SearchTarget.Directory && !isDir) continue;

            yield return itemPath;
        }
    }

    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate)
    {
        EnsureConnected();
        var response = _client!.Propfind(path.FullName, new PropfindParameters { ApplyTo = ApplyTo.Propfind.ResourceAndChildren }).GetAwaiter().GetResult();
        if (!response.IsSuccessful) yield break;

        foreach (var resource in response.Resources)
        {
            var name = Uri.UnescapeDataString(resource.Uri.TrimEnd('/').Split('/').Last());
            if (string.IsNullOrEmpty(name) || name == path.GetName()) continue;

            var itemPath = path / name;
            var item = new FileSystemItem(this, itemPath, resource.IsCollection)
            {
                Length = resource.ContentLength ?? 0,
                LastWriteTime = resource.LastModifiedDate?.ToUniversalTime() ?? DateTime.MinValue
            };

            if (searchPredicate == null || searchPredicate(ref item))
                yield return item;
        }
    }

    #endregion

    #region IFileSystemLinkProvider

    public Task<Link?> GetLinkAsync(UPath path, LinkArgs args, CancellationToken ct = default)
    {
        var addition = (WebDavAddition)Addition;
        var url = addition.WebdavURL.TrimEnd('/') + path.FullName;
        return Task.FromResult<Link?>(new Link
        {
            URL = url,
            Header = string.IsNullOrEmpty(addition.Authorization) ? null
                : new Dictionary<string, string> { { "Authorization", addition.Authorization } }
        });
    }

    #endregion

    #region IFileSystemUploader

    public async Task<UPath> UploadAsync(UPath dstDir, Stream stream, string fileName, Action<double> progress, CancellationToken ct = default)
    {
        EnsureConnected();
        var remotePath = (dstDir / fileName).FullName;

        await _client!.PutFile(remotePath, stream);
        progress(1.0);
        return dstDir / fileName;
    }

    #endregion
}

/// <summary>
/// A stream that uploads to WebDAV on dispose.
/// </summary>
internal class WebDavWriteStream : MemoryStream
{
    private readonly WebDavClient _client;
    private readonly string _path;
    private bool _uploaded;

    public WebDavWriteStream(WebDavClient client, string path)
    {
        _client = client;
        _path = path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_uploaded)
        {
            _uploaded = true;
            Position = 0;
            _client.PutFile(_path, this).GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}

public class WebDavAddition
{
    public string RootFolderPath { get; set; } = "/";
    [System.ComponentModel.Description("WebDAV server URL")]
    [System.ComponentModel.DataAnnotations.Required]
    public string WebdavURL { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    [System.ComponentModel.Description("Proxy address")]
    public string? ProxyAddress { get; set; }
    [System.ComponentModel.Description("Authorization header")]
    public string? Authorization { get; set; }
    [System.ComponentModel.Description("WebDAV vendor type")]
    public string? Vendor { get; set; }
    [System.ComponentModel.Description("Skip TLS certificate verification")]
    public bool TlsInsecureSkipVerify { get; set; }
}
