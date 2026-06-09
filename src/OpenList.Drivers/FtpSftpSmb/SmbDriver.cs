using System.Text.Json;
using SMBLibrary;
using SMBLibrary.Client;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using SmbFileAttribute = SMBLibrary.FileAttributes;
using SmbCreateOptions = SMBLibrary.CreateOptions;
using SmbAccessMask = SMBLibrary.AccessMask;
using SmbShareAccess = SMBLibrary.ShareAccess;
using SmbCreateDisposition = SMBLibrary.CreateDisposition;
using SmbFileInformationClass = SMBLibrary.FileInformationClass;

namespace OpenList.Drivers.Smb;

public class SmbDriver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverCopy, IDriverRemove, IDriverPutResult, IDriverWithDetails
{
    private SMB2Client? _smbClient;
    private ISMBFileStore? _fileStore;
    private long _lastConnTime;
    private string _shareName = "";

    public override DriverConfig Config() => new()
    {
        Name = "SMB",
        LocalSort = true,
        OnlyProxy = true,
        DefaultRoot = ".",
        CheckStatus = true
    };

    public override object GetAddition() => JsonSerializer.Deserialize<SmbAddition>(_storage.Addition) ?? new SmbAddition();
    protected override Type? GetAdditionType() => typeof(SmbAddition);

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (SmbAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;
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

    public override async Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var result = new List<IObj>();
        var path = dir.GetPath().Replace('/', '\\');

        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            path,
            SmbAccessMask.GENERIC_READ,
            SmbFileAttribute.Normal,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_DIRECTORY_FILE,
            null);

        if (status != NTStatus.STATUS_SUCCESS) return result;

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
                    result.Add(new ObjItem
                    {
                        Name = name,
                        Size = (long)info.EndOfFile,
                        Modified = info.LastWriteTime,
                        CTime = info.CreationTime,
                        IsFolder = info.FileAttributes.HasFlag(SmbFileAttribute.Directory),
                        Path = string.Concat(dir.GetPath().TrimEnd('/'), "/", name)
                    });
                }
            }
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }

        return result;
    }

    public override async Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var smbPath = file.GetPath().Replace('/', '\\');

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
            // Get file size
            status = _fileStore.GetFileInformation(out var fileInfo, fileHandle, SmbFileInformationClass.FileStandardInformation);
            if (status != NTStatus.STATUS_SUCCESS) return null;

            var fileStdInfo = (FileStandardInformation)fileInfo;
            var fileSize = fileStdInfo.EndOfFile;

            // Download to temp file
            var tempDir = Path.Combine(Path.GetTempPath(), "openlist-smb");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + "_" + file.GetName());

            using (var fs = new FileStream(tempFile, FileMode.Create))
            {
                var offset = 0L;
                var bufferSize = 1024 * 1024; // 1MB chunks
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

    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var smbPath = path.Replace('/', '\\');

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

        if (status != NTStatus.STATUS_SUCCESS) return null;

        try
        {
            status = _fileStore.GetFileInformation(
                out var fileInfo,
                fileHandle,
                SmbFileInformationClass.FileStandardInformation);

            if (status == NTStatus.STATUS_SUCCESS && fileInfo is FileStandardInformation stdInfo)
            {
                return new ObjItem
                {
                    Name = Path.GetFileName(path),
                    Size = (long)stdInfo.EndOfFile,
                    IsFolder = stdInfo.Directory,
                    Path = path
                };
            }
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }

        return null;
    }

    public async Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var path = string.Concat(parentDir.GetPath().TrimEnd('/'), "/", dirName).Replace('/', '\\');

        _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            path,
            SmbAccessMask.GENERIC_WRITE,
            SmbFileAttribute.Directory,
            SmbShareAccess.Read,
            SmbCreateDisposition.FILE_CREATE,
            SmbCreateOptions.FILE_DIRECTORY_FILE,
            null);

        _fileStore.CloseFile(fileHandle);

        return new ObjItem
        {
            Name = dirName,
            IsFolder = true,
            Path = string.Concat(parentDir.GetPath().TrimEnd('/'), "/", dirName),
            Modified = DateTime.UtcNow,
            CTime = DateTime.UtcNow
        };
    }

    public async Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var srcPath = srcObj.GetPath().Replace('/', '\\');
        var dstPath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", srcObj.GetName()).Replace('/', '\\');

        RenameFile(srcPath, dstPath);
    }

    public async Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var srcPath = srcObj.GetPath().Replace('/', '\\');
        var parentPath = Path.GetDirectoryName(srcPath) ?? "\\";
        var dstPath = string.Concat(parentPath.TrimEnd('\\'), "\\", newName);

        RenameFile(srcPath, dstPath);
    }

    public async Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var srcPath = srcObj.GetPath().Replace('/', '\\');
        var dstPath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", srcObj.GetName()).Replace('/', '\\');

        if (srcObj.IsDir())
            await CopyDirectoryAsync(srcPath, dstPath, ct);
        else
            await CopyFileAsync(srcPath, dstPath, ct);
    }

    public async Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var path = obj.GetPath().Replace('/', '\\');

        // Use SetFileInformation with FileDispositionInformation to mark for deletion
        var status = _fileStore!.CreateFile(
            out var fileHandle,
            out _,
            path,
            SmbAccessMask.GENERIC_READ | SmbAccessMask.DELETE,
            obj.IsDir() ? SmbFileAttribute.Directory : SmbFileAttribute.Normal,
            SmbShareAccess.Read | SmbShareAccess.Write | SmbShareAccess.Delete,
            SmbCreateDisposition.FILE_OPEN,
            obj.IsDir() ? SmbCreateOptions.FILE_DIRECTORY_FILE : SmbCreateOptions.FILE_NON_DIRECTORY_FILE,
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

    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        await EnsureConnectedAsync();
        var remotePath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", file.Obj.GetName()).Replace('/', '\\');

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
            if (file.Stream != null)
            {
                var buffer = new byte[1024 * 1024];
                long offset = 0;
                int bytesRead;
                while ((bytesRead = await file.Stream.ReadAsync(buffer, ct)) > 0)
                {
                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);
                    _fileStore.WriteFile(out _, fileHandle, offset, data);
                    offset += bytesRead;
                    if (file.Size > 0)
                        updateProgress((double)offset / file.Size);
                }
            }
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }

        return new ObjItem
        {
            Name = file.Obj.GetName(),
            IsFolder = false,
            Path = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", file.Obj.GetName()),
            Size = file.Size,
            Modified = DateTime.UtcNow,
            CTime = DateTime.UtcNow
        };
    }

    public Task<StorageDetails?> GetDetailsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<StorageDetails?>(null);
    }

    private void RenameFile(string srcPath, string dstPath)
    {
        // Open the source file with DELETE access for rename
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

    private Task EnsureConnectedAsync()
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastConnTime > 300)
        {
            DisconnectSmb();
            var addition = (SmbAddition)GetAddition();
            ConnectSmb(addition);
        }
        return Task.CompletedTask;
    }

    private async Task CopyFileAsync(string srcPath, string dstPath, CancellationToken ct)
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

    private async Task CopyDirectoryAsync(string srcPath, string dstPath, CancellationToken ct)
    {
        _fileStore!.CreateFile(out var dirHandle, out _,
            dstPath, SmbAccessMask.GENERIC_WRITE, SmbFileAttribute.Directory,
            SmbShareAccess.Read, SmbCreateDisposition.FILE_CREATE,
            SmbCreateOptions.FILE_DIRECTORY_FILE, null);
        _fileStore.CloseFile(dirHandle);

        _fileStore.CreateFile(out var listHandle, out _,
            srcPath, SmbAccessMask.GENERIC_READ, SmbFileAttribute.Normal,
            SmbShareAccess.Read, SmbCreateDisposition.FILE_OPEN,
            SmbCreateOptions.FILE_DIRECTORY_FILE, null);

        var status = _fileStore.QueryDirectory(out var results, listHandle, "*",
            SmbFileInformationClass.FileIdBothDirectoryInformation);
        _fileStore.CloseFile(listHandle);

        if (status == NTStatus.STATUS_SUCCESS)
        {
            foreach (FileIdBothDirectoryInformation info in results)
            {
                if (info.FileName == "." || info.FileName == "..") continue;
                var srcItem = string.Concat(srcPath.TrimEnd('\\'), "\\", info.FileName);
                var dstItem = string.Concat(dstPath.TrimEnd('\\'), "\\", info.FileName);

                if (info.FileAttributes.HasFlag(SmbFileAttribute.Directory))
                    await CopyDirectoryAsync(srcItem, dstItem, ct);
                else
                    await CopyFileAsync(srcItem, dstItem, ct);
            }
        }
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
