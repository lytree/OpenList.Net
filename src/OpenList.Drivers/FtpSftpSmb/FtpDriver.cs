using System.Text.Json;
using FluentFTP;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Ftp;

public class FtpDriver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverRemove, IDriverPutResult
{
    private AsyncFtpClient? _client;

    public override DriverConfig Config() => new()
    {
        Name = "FTP",
        DefaultRoot = "/",
        CheckStatus = true
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
        var items = await _client!.GetListing(dir.GetPath(), ct);

        var result = new List<IObj>();
        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            result.Add(new ObjItem
            {
                Name = item.Name,
                Size = item.Size,
                Modified = item.Modified,
                CTime = item.Modified,
                IsFolder = item.Type == FtpObjectType.Directory,
                Path = string.Concat(dir.GetPath().TrimEnd('/'), "/", item.Name)
            });
        }
        return result;
    }

    public override async Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        return null;
    }

    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var parentPath = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "/";
        var name = Path.GetFileName(path);
        var items = await _client!.GetListing(parentPath, ct);
        var item = items.FirstOrDefault(i => i.Name == name);
        if (item == null) return null;
        return new ObjItem
        {
            Name = item.Name,
            Size = item.Size,
            Modified = item.Modified,
            CTime = item.Modified,
            IsFolder = item.Type == FtpObjectType.Directory,
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
