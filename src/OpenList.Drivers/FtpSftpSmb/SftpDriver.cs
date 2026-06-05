using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Sftp;

public class SftpDriver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverRemove, IDriverPutResult, IDriverWithDetails
{
    private SftpClient? _client;
    private Exception? _connectionError;

    public override DriverConfig Config() => new()
    {
        Name = "SFTP",
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
            new DriverItem { Name = "address", Type = "string", Required = true, Help = "SFTP server address (host:port)" },
            new DriverItem { Name = "username", Type = "string", Required = true, Help = "Username" },
            new DriverItem { Name = "password", Type = "string", Help = "Password" },
            new DriverItem { Name = "private_key", Type = "text", Help = "Private key content" },
            new DriverItem { Name = "passphrase", Type = "string", Help = "Private key passphrase" },
            new DriverItem { Name = "ignore_symlink_error", Type = "bool", Default = "false", Help = "Ignore symlink errors" }
        ],
        Config = Config()
    };

    public override object GetAddition() => JsonSerializer.Deserialize<SftpAddition>(_storage.Addition) ?? new SftpAddition();

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
        var items = await Task.Run(() => _client!.ListDirectory(dir.GetPath()), ct);
        var result = new List<IObj>();
        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            result.Add(FileToObj(item, dir.GetPath()));
        }
        return result;
    }

    public override async Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        await ReconnectIfNeededAsync();
        var path = file.GetPath();

        // Download to temp file and serve (SFTP doesn't support direct URL links)
        var tempFile = Path.Combine(Path.GetTempPath(), $"sftp_{Guid.NewGuid():N}_{file.GetName()}");
        try
        {
            using var remoteStream = _client!.OpenRead(path);
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
        var fullPath = string.Concat(RootFolderPath.TrimEnd('/'), "/", path.TrimStart('/'));
        if (!await Task.Run(() => _client!.Exists(fullPath), ct))
            return null;
        var attr = await Task.Run(() => _client!.GetAttributes(fullPath), ct);
        if (attr == null) return null;
        return new ObjItem
        {
            Name = Path.GetFileName(fullPath),
            Size = attr.Size,
            Modified = attr.LastWriteTime,
            CTime = attr.LastWriteTime,
            IsFolder = attr.IsDirectory,
            Path = fullPath
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
        // SFTP doesn't have a standard way to get disk usage
        // StatVFS is not supported by SSH.NET
        return Task.FromResult<StorageDetails?>(null);
    }

    private void InitClient()
    {
        var addition = (SftpAddition)GetAddition();
        ConnectionInfo connInfo;

        if (!string.IsNullOrEmpty(addition.PrivateKey))
        {
            var keyBytes = Convert.FromBase64String(addition.PrivateKey);
            using var keyStream = new MemoryStream(keyBytes);
            var keyFile = string.IsNullOrEmpty(addition.Passphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, addition.Passphrase);
            connInfo = new ConnectionInfo(addition.Address, addition.Username,
                new PrivateKeyAuthenticationMethod(addition.Username, keyFile));
        }
        else
        {
            connInfo = new ConnectionInfo(addition.Address, addition.Username,
                new PasswordAuthenticationMethod(addition.Username, addition.Password));
        }

        _client = new SftpClient(connInfo);
        _client.Connect();
        _connectionError = null;

        // Monitor connection errors
        Task.Run(async () =>
        {
            try { await Task.Delay(-1); } catch { }
        });
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

    private static IObj FileToObj(ISftpFile file, string parentPath)
    {
        return new ObjItem
        {
            Name = file.Name,
            Size = file.Length,
            Modified = file.LastWriteTime,
            CTime = file.LastWriteTime,
            IsFolder = file.IsDirectory,
            Path = string.Concat(parentPath.TrimEnd('/'), "/", file.Name)
        };
    }
}

public class SftpAddition
{
    public string RootFolderPath { get; set; } = "/";
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? PrivateKey { get; set; }
    public string? Passphrase { get; set; }
    public bool IgnoreSymlinkError { get; set; }
}
