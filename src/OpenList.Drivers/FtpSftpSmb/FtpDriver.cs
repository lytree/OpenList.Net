using System.Net;
using System.Text.Json;
using FluentFTP;
using FluentStorage;
using FluentStorage.Blobs;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Ftp;

public class FtpDriver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverRemove, IDriverPutResult
{
    private AsyncFtpClient? _client;
    private IBlobStorage _blobStorage = null!;

    public override DriverConfig Config() => new()
    {
        Name = "FTP",
        LocalSort = true,
        OnlyProxy = true,
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
            new DriverItem { Name = "address", Type = "string", Required = true, Help = "FTP server address (host:port)" },
            new DriverItem { Name = "username", Type = "string", Required = true, Help = "Username" },
            new DriverItem { Name = "password", Type = "string", Help = "Password" },
            new DriverItem { Name = "encoding", Type = "string", Default = "UTF-8", Help = "Encoding" }
        ],
        Config = Config()
    };

    public override object GetAddition() => JsonSerializer.Deserialize<FtpAddition>(_storage.Addition) ?? new FtpAddition();

    public override async Task InitAsync(CancellationToken ct = default)
    {
        var addition = (FtpAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;
        await EnsureConnectedAsync(ct);
    }

    public override async Task DropAsync(CancellationToken ct = default)
    {
        if (_client != null)
        {
            await _client.Disconnect(ct);
            _client.Dispose();
            _client = null;
        }
    }

    public override async Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var virtualPath = dir.GetPath();
        var relativePath = virtualPath.TrimStart('/');

        var result = new List<IObj>();
        var blobs = await _blobStorage.ListAsync(new ListOptions { FolderPath = relativePath }, ct);

        foreach (var blob in blobs)
        {
            if (blob.Name == "." || blob.Name == "..") continue;
            result.Add(new ObjItem
            {
                Name = blob.Name,
                Size = blob.Size ?? 0,
                Modified = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
                CTime = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
                IsFolder = blob.IsFolder,
                Path = string.Concat(virtualPath.TrimEnd('/'), "/", blob.Name)
            });
        }
        return result;
    }

    public override async Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var relativePath = file.GetPath().TrimStart('/');

        var tempFile = Path.Combine(Path.GetTempPath(), $"ftp_{Guid.NewGuid():N}_{file.GetName()}");
        try
        {
            using var remoteStream = await _blobStorage.OpenReadAsync(relativePath, ct);
            using var localStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write);
            await remoteStream.CopyToAsync(localStream, 81920, ct);
            return new Link
            {
                URL = tempFile,
                Header = new Dictionary<string, string> { { "Content-Type", "application/octet-stream" } }
            };
        }
        catch
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            return null;
        }
    }

    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var relativePath = path.TrimStart('/');

        var blob = await _blobStorage.GetBlobAsync(relativePath, ct);
        if (blob == null) return null;

        return new ObjItem
        {
            Name = blob.Name,
            Size = blob.Size ?? 0,
            Modified = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
            CTime = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
            IsFolder = blob.IsFolder,
            Path = path
        };
    }

    public async Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var path = string.Concat(parentDir.GetPath().TrimEnd('/'), "/", dirName);
        await _client!.CreateDirectory(path, ct);
        return new ObjItem
        {
            Name = dirName,
            IsFolder = true,
            Path = path,
            Modified = DateTime.UtcNow,
            CTime = DateTime.UtcNow
        };
    }

    public async Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var dstPath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", srcObj.GetName());
        await _client!.MoveFile(srcObj.GetPath(), dstPath, FtpRemoteExists.Overwrite, ct);
    }

    public async Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var parentPath = Path.GetDirectoryName(srcObj.GetPath())?.Replace('\\', '/') ?? "/";
        var dstPath = string.Concat(parentPath.TrimEnd('/'), "/", newName);
        await _client!.Rename(srcObj.GetPath(), dstPath, ct);
    }

    public async Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        if (obj.IsDir())
            await _client!.DeleteDirectory(obj.GetPath(), FtpListOption.Recursive, ct);
        else
            await _client!.DeleteFile(obj.GetPath(), ct);
    }

    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var remotePath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", file.Obj.GetName());

        if (file.Stream != null)
        {
            await _client!.UploadStream(file.Stream, remotePath, FtpRemoteExists.Overwrite,
                false, new Progress<FtpProgress>(p =>
                {
                    if (file.Size > 0)
                        updateProgress((double)p.Progress / 100.0);
                }), ct);
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

    private async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_client != null && _client.IsConnected) return;

        var addition = (FtpAddition)GetAddition();
        _client = new AsyncFtpClient(addition.Address, addition.Username, addition.Password);
        _client.Config.DataConnectionConnectTimeout = 10000;
        _client.Config.ReadTimeout = 10000;
        await _client.Connect(ct);

        _blobStorage = StorageFactory.Blobs.FtpFromFluentFtpClient(_client);
    }
}

public class FtpAddition
{
    public string RootFolderPath { get; set; } = "/";
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Encoding { get; set; }
}
