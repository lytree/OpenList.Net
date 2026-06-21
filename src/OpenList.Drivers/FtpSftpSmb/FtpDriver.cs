using System.Text.Json;
using FluentFTP;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.Ftp;

public class FtpDriver : ZioDriverBase, IFileSystemLinkProvider, IFileSystemUploader
{
    private AsyncFtpClient? _client;

    public override DriverConfig Config { get; } = new()
    {
        Name = "FTP",
        LocalSort = true,
        OnlyProxy = true,
        DefaultRoot = "/",
        CheckStatus = true
    };

    private FtpAddition? _addition;
    public override object Addition => _addition ??= JsonSerializer.Deserialize<FtpAddition>(_storage?.Addition ?? "{}") ?? new FtpAddition();

    public override DriverInfo GetDriverInfo()
    {
        var additionalItems = BuildItemsFromObject(new FtpAddition());
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

    public override async Task InitAsync(CancellationToken ct = default)
    {
        var addition = (FtpAddition)Addition;
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

    #region IFileSystem Impl

    protected override void CreateDirectoryImpl(UPath path)
    {
        EnsureConnected();
        _client!.CreateDirectory(path.FullName).GetAwaiter().GetResult();
    }

    protected override bool DirectoryExistsImpl(UPath path)
    {
        EnsureConnected();
        return _client!.DirectoryExists(path.FullName).GetAwaiter().GetResult();
    }

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        EnsureConnected();
        _client!.MoveDirectory(srcPath.FullName, destPath.FullName).GetAwaiter().GetResult();
    }

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        EnsureConnected();
        _client!.DeleteDirectory(path.FullName, FtpListOption.Recursive).GetAwaiter().GetResult();
    }

    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        EnsureConnected();
        // FTP doesn't have a native copy - download then upload
        using var stream = _client!.OpenRead(srcPath.FullName).Result;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        _client.UploadStream(ms, destPath.FullName, overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip).GetAwaiter().GetResult();
    }

    protected override long GetFileLengthImpl(UPath path)
    {
        EnsureConnected();
        return _client!.GetFileSize(path.FullName).GetAwaiter().GetResult();
    }

    protected override bool FileExistsImpl(UPath path)
    {
        EnsureConnected();
        return _client!.FileExists(path.FullName).GetAwaiter().GetResult();
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        EnsureConnected();
        _client!.MoveFile(srcPath.FullName, destPath.FullName, FtpRemoteExists.Overwrite).GetAwaiter().GetResult();
    }

    protected override void DeleteFileImpl(UPath path)
    {
        EnsureConnected();
        _client!.DeleteFile(path.FullName).GetAwaiter().GetResult();
    }

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        EnsureConnected();
        if (access.HasFlag(FileAccess.Read))
            return _client!.OpenRead(path.FullName).GetAwaiter().GetResult();
        return _client!.OpenWrite(path.FullName).GetAwaiter().GetResult();
    }

    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        EnsureConnected();
        return _client!.GetModifiedTime(path.FullName).GetAwaiter().GetResult().ToUniversalTime();
    }

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        EnsureConnected();
        var items = _client!.GetListing(path.FullName, FtpListOption.Auto).GetAwaiter().GetResult();
        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            var itemPath = path / item.Name;
            var isDir = item.Type == FtpObjectType.Directory;

            if (searchTarget == SearchTarget.File && isDir) continue;
            if (searchTarget == SearchTarget.Directory && !isDir) continue;

            if (searchPattern == "*" || MatchesPattern(item.Name, searchPattern))
                yield return itemPath;
        }
    }

    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate)
    {
        EnsureConnected();
        var items = _client!.GetListing(path.FullName, FtpListOption.Auto).GetAwaiter().GetResult();
        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            var isDir = item.Type == FtpObjectType.Directory;
            var fsItem = new FileSystemItem(this, path / item.Name, isDir);
            if (searchPredicate == null || searchPredicate(ref fsItem))
                yield return fsItem;
        }
    }

    #endregion

    #region IFileSystemLinkProvider

    public async Task<Link?> GetLinkAsync(UPath path, LinkArgs args, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        // FTP doesn't support direct URLs - proxy through temp file
        var tempFile = Path.Combine(Path.GetTempPath(), $"ftp_{Guid.NewGuid():N}_{path.GetName()}");
        try
        {
            using var remoteStream = await _client!.OpenRead(path.FullName, token: ct);
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
        await EnsureConnectedAsync(ct);
        var remotePath = (dstDir / fileName).FullName;

        await _client!.UploadStream(stream, remotePath, FtpRemoteExists.Overwrite,
            false, new Progress<FtpProgress>(p =>
            {
                progress((double)p.Progress / 100.0);
            }), ct);

        progress(1.0);
        return new UPath(remotePath);
    }

    #endregion

    #region Helpers

    private async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_client != null && _client.IsConnected) return;

        var addition = (FtpAddition)Addition;
        _client = new AsyncFtpClient(addition.Address, addition.Username, addition.Password);
        _client.Config.DataConnectionConnectTimeout = 10000;
        _client.Config.ReadTimeout = 10000;
        await _client.Connect(ct);
    }

    private void EnsureConnected()
    {
        EnsureConnectedAsync().GetAwaiter().GetResult();
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

public class FtpAddition
{
    public string RootFolderPath { get; set; } = "/";
    [System.ComponentModel.Description("FTP server address (host:port)")]
    [System.ComponentModel.DataAnnotations.Required]
    public string Address { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required]
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    [System.ComponentModel.Description("Encoding")]
    public string? Encoding { get; set; }
}
