using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.Sftp;

public class SftpDriver : ZioDriverBase, IFileSystemLinkProvider, IFileSystemUploader
{
    private SftpClient? _client;
    private Exception? _connectionError;

    public override DriverConfig Config { get; } = new()
    {
        Name = "SFTP",
        LocalSort = true,
        OnlyProxy = true,
        DefaultRoot = "/",
        CheckStatus = true
    };

    private SftpAddition? _addition;
    public override object Addition => _addition ??= JsonSerializer.Deserialize<SftpAddition>(_storage?.Addition ?? "{}") ?? new SftpAddition();

    public override DriverInfo GetDriverInfo()
    {
        var additionalItems = BuildItemsFromObject(new SftpAddition());
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

    #region IFileSystem Impl

    protected override void CreateDirectoryImpl(UPath path) => _client!.CreateDirectory(path.FullName);

    protected override bool DirectoryExistsImpl(UPath path)
    {
        try { return _client!.GetAttributes(path.FullName).IsDirectory; }
        catch { return false; }
    }

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath) =>
        _client!.RenameFile(srcPath.FullName, destPath.FullName);

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        if (isRecursive)
            RemoveDirectoryRecursive(path.FullName);
        else
            _client!.DeleteDirectory(path.FullName);
    }

    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        using var stream = _client!.OpenRead(srcPath.FullName);
        using var dest = _client.OpenWrite(destPath.FullName);
        stream.CopyTo(dest);
    }

    protected override long GetFileLengthImpl(UPath path) =>
        _client!.GetAttributes(path.FullName).Size;

    protected override bool FileExistsImpl(UPath path)
    {
        try
        {
            var attrs = _client!.GetAttributes(path.FullName);
            return !attrs.IsDirectory;
        }
        catch { return false; }
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath) =>
        _client!.RenameFile(srcPath.FullName, destPath.FullName);

    protected override void DeleteFileImpl(UPath path) => _client!.DeleteFile(path.FullName);

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var sftpMode = mode switch
        {
            FileMode.Open => System.IO.FileMode.Open,
            FileMode.Create => System.IO.FileMode.Create,
            FileMode.CreateNew => System.IO.FileMode.CreateNew,
            FileMode.Append => System.IO.FileMode.Append,
            _ => System.IO.FileMode.Open
        };
        return _client!.Open(path.FullName, sftpMode);
    }

    protected override FileAttributes GetAttributesImpl(UPath path)
    {
        var attrs = _client!.GetAttributes(path.FullName);
        var result = FileAttributes.Normal;
        if (attrs.IsDirectory) result |= FileAttributes.Directory;
        return result;
    }

    protected override DateTime GetLastWriteTimeImpl(UPath path) =>
        _client!.GetLastWriteTimeUtc(path.FullName);

    protected override DateTime GetCreationTimeImpl(UPath path) =>
        _client!.GetLastAccessTimeUtc(path.FullName);

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        var items = _client!.ListDirectory(path.FullName);
        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            var itemPath = path / item.Name;
            var isDir = item.IsDirectory;

            if (searchTarget == SearchTarget.File && isDir) continue;
            if (searchTarget == SearchTarget.Directory && !isDir) continue;

            if (searchPattern == "*" || MatchesPattern(item.Name, searchPattern))
                yield return itemPath;
        }
    }

    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate)
    {
        var items = _client!.ListDirectory(path.FullName);
        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            var fsItem = new FileSystemItem(this, path / item.Name, item.IsDirectory);
            if (searchPredicate == null || searchPredicate(ref fsItem))
                yield return fsItem;
        }
    }

    #endregion

    #region IFileSystemLinkProvider

    public async Task<Link?> GetLinkAsync(UPath path, LinkArgs args, CancellationToken ct = default)
    {
        await ReconnectIfNeededAsync();
        var tempFile = Path.Combine(Path.GetTempPath(), $"sftp_{Guid.NewGuid():N}_{path.GetName()}");
        try
        {
            using var remoteStream = _client!.OpenRead(path.FullName);
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

    #endregion

    #region IFileSystemUploader

    public async Task<UPath> UploadAsync(UPath dstDir, Stream stream, string fileName, Action<double> progress, CancellationToken ct = default)
    {
        await ReconnectIfNeededAsync();
        var remotePath = (dstDir / fileName).FullName;

        using var remoteStream = _client!.Create(remotePath);
        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await remoteStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            if (stream.CanSeek)
                progress((double)totalRead / stream.Length);
        }
        progress(1.0);
        return new UPath(remotePath);
    }

    #endregion

    #region Helpers

    private void InitClient()
    {
        var addition = (SftpAddition)Addition;
        ConnectionInfo connInfo;

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

    private void RemoveDirectoryRecursive(string path)
    {
        var items = _client!.ListDirectory(path);
        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            var itemPath = path.TrimEnd('/') + "/" + item.Name;
            if (item.IsDirectory)
                RemoveDirectoryRecursive(itemPath);
            else
                _client.DeleteFile(itemPath);
        }
        _client.DeleteDirectory(path);
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith('*')) return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith('*')) return name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
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
