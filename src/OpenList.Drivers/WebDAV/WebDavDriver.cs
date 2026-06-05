using System.Text.Json;
using WebDav;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.WebDAV;

public class WebDavDriver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverCopy, IDriverRemove, IDriverPutResult
{
    private WebDavClient? _client;

    public override DriverConfig Config() => new()
    {
        Name = "WebDAV",
        DefaultRoot = "/",
        CheckStatus = true
    };

    public override DriverInfo Info() => new()
    {
        Common =
        [
            new DriverItem { Name = "root_folder_path", Type = "string", Default = "/", Required = true, Help = "Root folder path" }
        ],
        Additional =
        [
            new DriverItem { Name = "webdav_url", Type = "string", Required = true, Help = "WebDAV server URL" },
            new DriverItem { Name = "username", Type = "string", Help = "Username" },
            new DriverItem { Name = "password", Type = "string", Help = "Password" },
            new DriverItem { Name = "proxy_address", Type = "string", Help = "Proxy address" },
            new DriverItem { Name = "authorization", Type = "string", Help = "Authorization header" },
            new DriverItem { Name = "vendor", Type = "string", Help = "WebDAV vendor type" },
            new DriverItem { Name = "tls_insecure_skip_verify", Type = "bool", Default = "false", Help = "Skip TLS certificate verification" }
        ],
        Config = Config()
    };

    public override object GetAddition() => JsonSerializer.Deserialize<WebDavAddition>(_storage.Addition) ?? new WebDavAddition();

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (WebDavAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;

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
        _client = null;
        return Task.CompletedTask;
    }

    public override async Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        var result = new List<IObj>();
        var response = await _client!.Propfind(dir.GetPath());

        if (!response.IsSuccessful) return result;

        foreach (var resource in response.Resources)
        {
            var name = Uri.UnescapeDataString(resource.Uri.TrimEnd('/').Split('/').Last());
            if (string.IsNullOrEmpty(name) || name == dir.GetPath().TrimEnd('/').Split('/').Last()) continue;

            result.Add(new ObjItem
            {
                Name = name,
                Size = resource.ContentLength ?? 0,
                Modified = resource.LastModifiedDate ?? DateTime.MinValue,
                CTime = resource.CreationDate ?? resource.LastModifiedDate ?? DateTime.MinValue,
                IsFolder = resource.IsCollection,
                Path = resource.Uri.TrimEnd('/')
            });
        }

        return result;
    }

    public override async Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        var addition = (WebDavAddition)GetAddition();
        var url = addition.WebdavURL.TrimEnd('/') + file.GetPath();
        return new Link
        {
            URL = url,
            Header = string.IsNullOrEmpty(addition.Authorization) ? null
                : new Dictionary<string, string> { { "Authorization", addition.Authorization } }
        };
    }

    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        var fullPath = string.Concat(RootFolderPath.TrimEnd('/'), "/", path.TrimStart('/'));
        var response = await _client!.Propfind(fullPath);

        if (!response.IsSuccessful) return null;

        var resource = response.Resources.FirstOrDefault(r =>
            r.Uri.TrimEnd('/') == fullPath.TrimEnd('/'));

        if (resource == null) return null;

        var name = Uri.UnescapeDataString(resource.Uri.TrimEnd('/').Split('/').Last());
        return new ObjItem
        {
            Name = name,
            Size = resource.ContentLength ?? 0,
            Modified = resource.LastModifiedDate ?? DateTime.MinValue,
            CTime = resource.CreationDate ?? resource.LastModifiedDate ?? DateTime.MinValue,
            IsFolder = resource.IsCollection,
            Path = fullPath
        };
    }

    public async Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        var path = string.Concat(parentDir.GetPath().TrimEnd('/'), "/", dirName, "/");
        await _client!.Mkcol(path);
        return new ObjItem
        {
            Name = dirName,
            IsFolder = true,
            Path = path.TrimEnd('/'),
            Modified = DateTime.UtcNow,
            CTime = DateTime.UtcNow
        };
    }

    public async Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var dstPath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", srcObj.GetName());
        if (srcObj.IsDir()) dstPath += "/";
        await _client!.Move(srcObj.GetPath(), dstPath, new MoveParameters { Overwrite = true });
    }

    public async Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        var parentPath = srcObj.GetPath().TrimEnd('/')[..srcObj.GetPath().TrimEnd('/').LastIndexOf('/')];
        var dstPath = string.Concat(parentPath, "/", newName);
        if (srcObj.IsDir()) dstPath += "/";
        await _client!.Move(srcObj.GetPath(), dstPath, new MoveParameters { Overwrite = true });
    }

    public async Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var dstPath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", srcObj.GetName());
        if (srcObj.IsDir()) dstPath += "/";
        await _client!.Copy(srcObj.GetPath(), dstPath, new CopyParameters { Overwrite = true });
    }

    public async Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        await _client!.Delete(obj.GetPath());
    }

    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        var remotePath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", file.Obj.GetName());

        if (file.Stream != null)
        {
            await _client!.PutFile(remotePath, file.Stream);
        }

        return new ObjItem
        {
            Name = file.Obj.GetName(),
            IsFolder = false,
            Path = remotePath,
            Size = file.Size,
            Modified = DateTime.UtcNow,
            CTime = DateTime.UtcNow
        };
    }
}

public class WebDavAddition
{
    public string RootFolderPath { get; set; } = "/";
    public string WebdavURL { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ProxyAddress { get; set; }
    public string? Authorization { get; set; }
    public string? Vendor { get; set; }
    public bool TlsInsecureSkipVerify { get; set; }
}
