using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using FluentStorage;
using FluentStorage.Blobs;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using OpenList.Core.Interfaces;

namespace OpenList.Drivers.Sftp;

public class SftpDriver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverRemove, IDriverPutResult, IDriverWithDetails
{
    private SftpClient? _client;
    private IBlobStorage _blobStorage = null!;
    private Exception? _connectionError;

    public override DriverConfig Config() => new()
    {
        Name = "SFTP",
        LocalSort = true,
        OnlyProxy = true,
        DefaultRoot = "/",
        CheckStatus = true
    };

    public override object GetAddition() => JsonSerializer.Deserialize<SftpAddition>(_storage.Addition) ?? new SftpAddition();
    protected override Type? GetAdditionType() => typeof(SftpAddition);

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (SftpAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;
        InitClient();
        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default)
    {
        _client?.Disconnect();
        _client?.Dispose();
        _client = null;
        return Task.CompletedTask;
    }

    public override async Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        await ReconnectIfNeededAsync();
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
        await ReconnectIfNeededAsync();
        var relativePath = file.GetPath().TrimStart('/');

        var tempFile = Path.Combine(Path.GetTempPath(), $"sftp_{Guid.NewGuid():N}_{file.GetName()}");
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
        await ReconnectIfNeededAsync();
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
        await ReconnectIfNeededAsync();
        var path = string.Concat(parentDir.GetPath().TrimEnd('/'), "/", dirName);
        await Task.Run(() => _client!.CreateDirectory(path), ct);
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
        await ReconnectIfNeededAsync();
        var dstPath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", srcObj.GetName());
        await Task.Run(() => _client!.RenameFile(srcObj.GetPath(), dstPath), ct);
    }

    public async Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        await ReconnectIfNeededAsync();
        var parentPath = Path.GetDirectoryName(srcObj.GetPath())?.Replace('\\', '/') ?? "/";
        var dstPath = string.Concat(parentPath.TrimEnd('/'), "/", newName);
        await Task.Run(() => _client!.RenameFile(srcObj.GetPath(), dstPath), ct);
    }

    public async Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        await ReconnectIfNeededAsync();
        if (obj.IsDir())
            await RemoveDirectoryRecursiveAsync(obj.GetPath(), ct);
        else
            await Task.Run(() => _client!.DeleteFile(obj.GetPath()), ct);
    }

    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        await ReconnectIfNeededAsync();
        var remotePath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", file.Obj.GetName());

        if (file.Stream != null)
        {
            using var remoteStream = _client!.Create(remotePath);
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await file.Stream.ReadAsync(buffer, ct)) > 0)
            {
                await remoteStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;
                if (file.Size > 0)
                    updateProgress((double)totalRead / file.Size);
            }
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

    public Task<StorageDetails?> GetDetailsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<StorageDetails?>(null);
    }

    private void InitClient()
    {
        var addition = (SftpAddition)GetAddition();
        ConnectionInfo connInfo;

        // Parse host and port from address
        var parts = addition.Address.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 22;

        if (!string.IsNullOrEmpty(addition.PrivateKey))
        {
            var keyBytes = Convert.FromBase64String(addition.PrivateKey);
            using var keyStream = new MemoryStream(keyBytes);
            var keyFile = string.IsNullOrEmpty(addition.Passphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, addition.Passphrase);
            connInfo = new ConnectionInfo(host, port, addition.Username,
                new PrivateKeyAuthenticationMethod(addition.Username, keyFile));
        }
        else
        {
            connInfo = new ConnectionInfo(host, port, addition.Username,
                new PasswordAuthenticationMethod(addition.Username, addition.Password));
        }

        _client = new SftpClient(connInfo);
        _client.Connect();
        _connectionError = null;

        // Create IBlobStorage from the same connection info
        _blobStorage = StorageFactory.Blobs.Sftp(connInfo);
    }

    private async Task ReconnectIfNeededAsync()
    {
        if (_connectionError != null || _client == null || !_client.IsConnected)
        {
            _client?.Dispose();
            InitClient();
        }
        await Task.CompletedTask;
    }

    private async Task RemoveDirectoryRecursiveAsync(string path, CancellationToken ct)
    {
        var items = await Task.Run(() => _client!.ListDirectory(path), ct);
        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            var itemPath = string.Concat(path.TrimEnd('/'), "/", item.Name);
            if (item.IsDirectory)
                await RemoveDirectoryRecursiveAsync(itemPath, ct);
            else
                await Task.Run(() => _client!.DeleteFile(itemPath), ct);
        }
        await Task.Run(() => _client!.DeleteDirectory(path), ct);
    }
}

public class SftpAddition
{
    public string RootFolderPath { get; set; } = "/";
    [System.ComponentModel.Description("SFTP server address (host:port)")]
    [System.ComponentModel.DataAnnotations.Required]
    public string Address { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required]
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    [System.ComponentModel.Description("Private key content")]
    [DriverItemType("text")]
    public string? PrivateKey { get; set; }
    [System.ComponentModel.Description("Private key passphrase")]
    public string? Passphrase { get; set; }
    [System.ComponentModel.Description("Ignore symlink errors")]
    public bool IgnoreSymlinkError { get; set; }
}
