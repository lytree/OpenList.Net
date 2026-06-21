using System.Text.Json;
using SMBLibrary;
using SMBLibrary.Client;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;
using SmbFileAttribute = SMBLibrary.FileAttributes;
using SmbCreateOptions = SMBLibrary.CreateOptions;
using SmbAccessMask = SMBLibrary.AccessMask;
using SmbShareAccess = SMBLibrary.ShareAccess;
using SmbCreateDisposition = SMBLibrary.CreateDisposition;
using SmbFileInformationClass = SMBLibrary.FileInformationClass;

namespace OpenList.Drivers.Smb;

public class SmbDriver : ZioDriverBase, IFileSystemLinkProvider, IFileSystemUploader, IFileSystemWithDetails
{
    private SMB2Client? _smbClient;
    private ISMBFileStore? _fileStore;
    private long _lastConnTime;
    private string _shareName = "";
    private string _rootFolderPath = "/";

    public override DriverConfig Config { get; } = new()
    {
        Name = "SMB",
        LocalSort = true,
        OnlyProxy = true,
        DefaultRoot = ".",
        CheckStatus = true
    };

    private SmbAddition? _addition;
    public override object Addition => _addition ??= JsonSerializer.Deserialize<SmbAddition>(_storage?.Addition ?? "{}") ?? new SmbAddition();

    public override DriverInfo GetDriverInfo()
    {
        var additionalItems = BuildItemsFromObject(new SmbAddition());
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
        var addition = (SmbAddition)Addition;
        _rootFolderPath = addition.RootFolderPath;
        _shareName = addition.ShareName;

        if (!addition.Address.Contains(':'))
            addition.Address += ":445";

        ConnectSmb(addition);
        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default)
    {
        DisconnectSmb();
        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (_smbClient == null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastConnTime > 300)
        {
            DisconnectSmb();
            var addition = (SmbAddition)Addition;
            ConnectSmb(addition);
        }
    }

    #region IFileSystem Impl

    protected override void CreateDirectoryImpl(UPath path)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_WRITE,
            SmbFileAttribute.Directory,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_CREATE,
            SmbCreateOptions.FILE_DIRECTORY_FILE,
            null);

        _fileStore.CloseFile(fileHandle);
    }

    protected override bool DirectoryExistsImpl(UPath path)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) return false;

        _fileStore.CloseFile(fileHandle);
        return true;
    }

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        EnsureConnected();
        var src = srcPath.FullName.Replace('/', '\\');
        var dst = destPath.FullName.Replace('/', '\\');
        RenameFile(src, dst);
    }

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ | SmbAccessMask.DELETE,
            SmbFileAttribute.Directory,
            SmbShareAccess.Read | SmbShareAccess.Write | SmbShareAccess.Delete,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) return;

        try
        {
            var disposition = new FileDispositionInformation { DeletePending = true };
            _fileStore.SetFileInformation(fileHandle, disposition);
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }
    }

    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        EnsureConnected();
        var src = srcPath.FullName.Replace('/', '\\');
        var dst = destPath.FullName.Replace('/', '\\');
        CopyFileInternal(src, dst);
    }

    protected override long GetFileLengthImpl(UPath path)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) return 0;

        try
        {
            status = _fileStore.GetFileInformation(out var fileInfo, fileHandle, SmbFileInformationClass.FileStandardInformation);
            if (status == NTStatus.STATUS_SUCCESS && fileInfo is FileStandardInformation stdInfo)
                return (long)stdInfo.EndOfFile;
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }

        return 0;
    }

    protected override bool FileExistsImpl(UPath path)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) return false;

        _fileStore.CloseFile(fileHandle);
        return true;
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        EnsureConnected();
        var src = srcPath.FullName.Replace('/', '\\');
        var dst = destPath.FullName.Replace('/', '\\');
        RenameFile(src, dst);
    }

    protected override void DeleteFileImpl(UPath path)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ | SmbAccessMask.DELETE,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read | SmbShareAccess.Write | SmbShareAccess.Delete,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) return;

        try
        {
            var disposition = new FileDispositionInformation { DeletePending = true };
            _fileStore.SetFileInformation(fileHandle, disposition);
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }
    }

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        if (access.HasFlag(FileAccess.Read))
        {
            return OpenSmbRead(smbPath);
        }

        return new SmbWriteStream(this, smbPath);
    }

    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) return DateTime.MinValue;

        try
        {
            status = _fileStore.GetFileInformation(out var fileInfo, fileHandle, SmbFileInformationClass.FileBasicInformation);
            if (status == NTStatus.STATUS_SUCCESS && fileInfo is FileBasicInformation basicInfo)
            {
                var fileTime = basicInfo.LastWriteTime.ToFileTimeUtc();
                return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
            }
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }

        return DateTime.MinValue;
    }

    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) yield break;

        try
        {
            status = _fileStore.QueryDirectory(
                out var queryResults,
                fileHandle,
                "*",
                SmbFileInformationClass.FileIdBothDirectoryInformation);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                foreach (FileIdBothDirectoryInformation info in queryResults)
                {
                    var name = info.FileName;
                    if (name == "." || name == "..") continue;

                    var itemPath = path / name;
                    var item = new FileSystemItem(this, itemPath, info.FileAttributes.HasFlag(SmbFileAttribute.Directory))
                    {
                        Length = (long)info.EndOfFile,
                        LastWriteTime = info.LastWriteTime
                    };

                    if (searchPredicate == null || searchPredicate(ref item))
                        yield return item;
                }
            }
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }
    }

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        foreach (var item in EnumerateItemsImpl(path, searchOption, null))
        {
            if (searchTarget == SearchTarget.File && item.IsDirectory) continue;
            if (searchTarget == SearchTarget.Directory && !item.IsDirectory) continue;
            yield return item.Path;
        }
    }

    #endregion

    #region IFileSystemLinkProvider

    public async Task<Link?> GetLinkAsync(UPath path, LinkArgs args, CancellationToken ct = default)
    {
        EnsureConnected();
        var smbPath = path.FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) return null;

        try
        {
            status = _fileStore.GetFileInformation(out var fileInfo, fileHandle, SmbFileInformationClass.FileStandardInformation);
            if (status != NTStatus.STATUS_SUCCESS) return null;

            var fileStdInfo = (FileStandardInformation)fileInfo;
            var fileSize = fileStdInfo.EndOfFile;

            // Download to temp file
            var tempDir = Path.Combine(Path.GetTempPath(), "openlist-smb");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + "_" + path.GetName());

            using (var fs = new FileStream(tempFile, FileMode.Create))
            {
                var offset = 0L;
                var bufferSize = 1024 * 1024;
                while (offset < fileSize)
                {
                    var readSize = (int)Math.Min(bufferSize, fileSize - offset);
                    status = _fileStore.ReadFile(out var data, fileHandle, offset, readSize);
                    if (status != NTStatus.STATUS_SUCCESS) break;
                    if (data == null || data.Length == 0) break;
                    await fs.WriteAsync(data, 0, data.Length, ct);
                    offset += data.Length;
                }
            }

            _fileStore.CloseFile(fileHandle);

            return new Link
            {
                URL = tempFile,
                ContentLength = fileSize
            };
        }
        catch
        {
            _fileStore.CloseFile(fileHandle);
            return null;
        }
    }

    #endregion

    #region IFileSystemUploader

    public async Task<UPath> UploadAsync(UPath dstDir, Stream stream, string fileName, Action<double> progress, CancellationToken ct = default)
    {
        EnsureConnected();
        var remotePath = (dstDir / fileName).FullName.Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            remotePath,
            SmbAccessMask.GENERIC_WRITE | SmbAccessMask.SYNCHRONIZE,
            SmbFileAttribute.Normal,
            SmbShareAccess.None,
            SmbCreateDisposition.FILE_OVERWRITE_IF,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE | SmbCreateOptions.FILE_SYNCHRONOUS_IO_ALERT,
            null);

        if (status != NTStatus.STATUS_SUCCESS)
            throw new InvalidOperationException($"Failed to create file: {status}");

        try
        {
            var buffer = new byte[1024 * 1024];
            long offset = 0;
            long totalSize = stream.CanSeek ? stream.Length : -1;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
            {
                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                _fileStore.WriteFile(out _, fileHandle, offset, data);
                offset += bytesRead;
                if (totalSize > 0)
                    progress(offset / (double)totalSize);
            }
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }

        progress(1.0);
        return dstDir / fileName;
    }

    #endregion

    #region IFileSystemWithDetails

    public Task<StorageDetails?> GetDetailsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<StorageDetails?>(null);
    }

    #endregion

    #region SMB Helpers

    private Stream OpenSmbRead(string smbPath)
    {
        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            smbPath,
            SmbAccessMask.GENERIC_READ,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS)
            throw new FileNotFoundException($"File not found: {smbPath}");

        // Get file size
        status = _fileStore.GetFileInformation(out var fileInfo, fileHandle, SmbFileInformationClass.FileStandardInformation);
        var fileSize = status == NTStatus.STATUS_SUCCESS && fileInfo is FileStandardInformation stdInfo
            ? (long)stdInfo.EndOfFile
            : 0;

        return new SmbReadStream(_fileStore, fileHandle, fileSize);
    }

    private void RenameFile(string srcPath, string dstPath)
    {
        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            srcPath,
            SmbAccessMask.GENERIC_READ | SmbAccessMask.DELETE,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read | SmbShareAccess.Write | SmbShareAccess.Delete,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE | SmbCreateOptions.FILE_SYNCHRONOUS_IO_ALERT,
            null);

        if (status != NTStatus.STATUS_SUCCESS)
        {
            // Try as directory
            status = _fileStore.CreateFile(
                out fileHandle,
                out _,
                srcPath,
                SmbAccessMask.GENERIC_READ | SmbAccessMask.DELETE,
                SmbFileAttribute.Directory,
                SmbShareAccess.Read | SmbShareAccess.Write | SmbShareAccess.Delete,
                SmbCreateDisposition.FILE_OPEN,
                SmbCreateOptions.FILE_DIRECTORY_FILE,
                null);
        }

        if (status != NTStatus.STATUS_SUCCESS) return;

        try
        {
            var renameInfo = new FileRenameInformationType2
            {
                ReplaceIfExists = true,
                FileName = dstPath
            };
            _fileStore.SetFileInformation(fileHandle, renameInfo);
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }
    }

    private void CopyFileInternal(string srcPath, string dstPath)
    {
        var status = _fileStore!.CreateFile(out var srcHandle, out _,
            srcPath, SmbAccessMask.GENERIC_READ, SmbFileAttribute.Normal,
            SmbShareAccess.Read, SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE | SmbCreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS) return;

        status = _fileStore.CreateFile(out var dstHandle, out _,
            dstPath, SmbAccessMask.GENERIC_WRITE | SmbAccessMask.SYNCHRONIZE, SmbFileAttribute.Normal,
            SmbShareAccess.None, SmbCreateDisposition.FILE_OVERWRITE_IF,
            SmbCreateOptions.FILE_NON_DIRECTORY_FILE | SmbCreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            _fileStore.CloseFile(srcHandle);
            return;
        }

        try
        {
            long offset = 0;
            while (true)
            {
                status = _fileStore.ReadFile(out var data, srcHandle, offset, 1024 * 1024);
                if (status != NTStatus.STATUS_SUCCESS || data.Length == 0) break;
                _fileStore.WriteFile(out _, dstHandle, offset, data);
                offset += data.Length;
            }
        }
        finally
        {
            _fileStore.CloseFile(srcHandle);
            _fileStore.CloseFile(dstHandle);
        }
    }

    private void ConnectSmb(SmbAddition addition)
    {
        var address = addition.Address.Split(':')[0];

        _smbClient = new SMB2Client();
        _smbClient.Connect(address, SMBTransportType.DirectTCPTransport);
        _smbClient.Login(string.IsNullOrEmpty(addition.Domain) ? "" : addition.Domain,
            addition.Username, addition.Password);

        var shares = _smbClient.ListShares(out _);
        var shareName = shares.FirstOrDefault(s =>
            s.Equals(addition.ShareName, StringComparison.OrdinalIgnoreCase));
        if (shareName == null)
            throw new InvalidOperationException($"Share '{addition.ShareName}' not found");

        _fileStore = _smbClient.TreeConnect(shareName, out _);
        _lastConnTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private void DisconnectSmb()
    {
        _fileStore?.Disconnect();
        _smbClient?.Disconnect();
        _fileStore = null;
        _smbClient = null;
    }

    #endregion
}

/// <summary>
/// A read-only stream that reads from SMB in chunks.
/// </summary>
internal class SmbReadStream : Stream
{
    private readonly ISMBFileStore _fileStore;
    private readonly object _fileHandle;
    private readonly long _length;
    private long _position;

    public SmbReadStream(ISMBFileStore fileStore, object fileHandle, long length)
    {
        _fileStore = fileStore;
        _fileHandle = fileHandle;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var readSize = (int)Math.Min(count, _length - _position);
        if (readSize <= 0) return 0;

        var status = _fileStore.ReadFile(out var data, _fileHandle, _position, readSize);
        if (status != NTStatus.STATUS_SUCCESS || data == null || data.Length == 0) return 0;

        Array.Copy(data, 0, buffer, offset, data.Length);
        _position += data.Length;
        return data.Length;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await Task.Run(() => Read(buffer, offset, count), cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fileStore.CloseFile(_fileHandle);
        }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A write stream that uploads to SMB on dispose.
/// </summary>
internal class SmbWriteStream : MemoryStream
{
    private readonly SmbDriver _driver;
    private readonly string _smbPath;
    private bool _written;

    public SmbWriteStream(SmbDriver driver, string smbPath)
    {
        _driver = driver;
        _smbPath = smbPath;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_written)
        {
            _written = true;
            Position = 0;
            _driver.UploadAsync(UPath.Root, this, _smbPath, _ => { }).GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}

public class SmbAddition
{
    public string RootFolderPath { get; set; } = "/";
    [System.ComponentModel.Description("SMB server address (host:port)")]
    [System.ComponentModel.DataAnnotations.Required]
    public string Address { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required]
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    [System.ComponentModel.Description("Share name")]
    [System.ComponentModel.DataAnnotations.Required]
    public string ShareName { get; set; } = "";
    [System.ComponentModel.Description("Domain")]
    public string? Domain { get; set; }
}
