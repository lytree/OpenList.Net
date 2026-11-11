using OpenList.Vfs;
using SMBLibrary;
using SMBLibrary.Client;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// 基于 SMBLibrary 客户端（SMB2）的文件系统。
/// SMB2 客户端不是线程安全的，所有共享操作都经 <see cref="_gate"/> 串行化。
/// </summary>
internal sealed class SmbFileSystem : NetworkFileSystemBase
{
    private readonly SMB2Client _client;
    private readonly ISMBFileStore _store;
    private readonly string _rootPrefix;
    private readonly object _gate = new();

    /// <summary>用已登录的客户端与共享连接构造文件系统。</summary>
    /// <param name="client">已登录的 SMB2 客户端。</param>
    /// <param name="store">已建立树连接的共享。</param>
    /// <param name="rootFolderPath">共享内的根路径，可为空。</param>
    internal SmbFileSystem(SMB2Client client, ISMBFileStore store, string? rootFolderPath)
    {
        _client = client;
        _store = store;
        _rootPrefix = (rootFolderPath ?? "").Replace('/', '\\').Trim('\\');
    }

    /// <summary>把 VFS 路径转换为 SMB 共享内路径（反斜杠分隔，无前导斜杠）。</summary>
    private string ToServerPath(UPath path)
    {
        var relative = path.FullName.Trim('/').Replace('/', '\\');
        if (_rootPrefix.Length == 0)
            return relative;
        return relative.Length == 0 ? _rootPrefix : _rootPrefix + "\\" + relative;
    }

    /// <summary>打开共享内路径，返回 NT 状态码。</summary>
    private NTStatus Open(string serverPath, AccessMask access, CreateDisposition disposition,
        CreateOptions options, out object handle, out FileStatus fileStatus)
    {
        lock (_gate)
        {
            return _store.CreateFile(out handle, out fileStatus, serverPath, access,
                SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write,
                disposition, options, null);
        }
    }

    private void CloseRaw(object handle)
    {
        lock (_gate)
        {
            _store.CloseFile(handle);
        }
    }

    private static bool IsNotFound(NTStatus status)
        => status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
            or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND
            or NTStatus.STATUS_NO_SUCH_FILE;

    /// <summary>把非成功状态码转换为异常。</summary>
    private static IOException Error(NTStatus status, UPath path)
        => new($"SMB operation on '{path}' failed: {status}");

    // --- 存在性 ---

    /// <summary>目录是否存在：尝试以目录选项打开。</summary>
    protected override bool DirectoryExistsImpl(UPath path)
    {
        var status = Open(ToServerPath(path), AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, out var handle, out _);
        if (status != NTStatus.STATUS_SUCCESS)
            return false;
        CloseRaw(handle);
        return true;
    }

    /// <summary>文件是否存在：尝试以非目录选项打开。</summary>
    protected override bool FileExistsImpl(UPath path)
    {
        var status = Open(ToServerPath(path), AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE, out var handle, out _);
        if (status != NTStatus.STATUS_SUCCESS)
            return false;
        CloseRaw(handle);
        return true;
    }

    // --- 目录管理 ---

    /// <summary>创建目录；已存在时忽略（与本地文件系统语义一致）。</summary>
    protected override void CreateDirectoryImpl(UPath path)
    {
        var status = Open(ToServerPath(path), AccessMask.GENERIC_WRITE, CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_DIRECTORY_FILE, out var handle, out _);
        if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
            return;
        if (status != NTStatus.STATUS_SUCCESS)
            throw Error(status, path);
        CloseRaw(handle);
    }

    /// <summary>删除文件：打开后设置删除挂起标志。</summary>
    protected override void DeleteFileImpl(UPath path)
    {
        var status = Open(ToServerPath(path), AccessMask.DELETE, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE, out var handle, out _);
        if (IsNotFound(status))
            throw new FileNotFoundException($"File not found: {path}");
        if (status != NTStatus.STATUS_SUCCESS)
            throw Error(status, path);

        try
        {
            lock (_gate)
            {
                status = _store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
            }
            if (status != NTStatus.STATUS_SUCCESS)
                throw Error(status, path);
        }
        finally
        {
            CloseRaw(handle);
        }
    }

    /// <summary>删除目录：递归时先清空子项，再对目录设置删除挂起标志。</summary>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        if (isRecursive)
        {
            foreach (var child in EnumeratePaths(path, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both).ToList())
            {
                if (DirectoryExists(child))
                    DeleteDirectoryImpl(child, isRecursive: true);
                else
                    DeleteFileImpl(child);
            }
        }

        var status = Open(ToServerPath(path), AccessMask.DELETE, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, out var handle, out _);
        if (IsNotFound(status))
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        if (status != NTStatus.STATUS_SUCCESS)
            throw Error(status, path);

        try
        {
            lock (_gate)
            {
                status = _store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
            }
            if (status != NTStatus.STATUS_SUCCESS)
                throw Error(status, path);
        }
        finally
        {
            CloseRaw(handle);
        }
    }

    // --- 文件打开 ---

    /// <summary>打开文件并返回可在给定偏移读写的流。</summary>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var disposition = mode switch
        {
            FileMode.Open => CreateDisposition.FILE_OPEN,
            FileMode.Create => CreateDisposition.FILE_OVERWRITE_IF,
            FileMode.OpenOrCreate => CreateDisposition.FILE_OPEN_IF,
            FileMode.CreateNew => CreateDisposition.FILE_CREATE,
            FileMode.Truncate => CreateDisposition.FILE_OVERWRITE,
            FileMode.Append => CreateDisposition.FILE_OPEN_IF,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        var wantsWrite = access.HasFlag(FileAccess.Write);
        var mask = wantsWrite
            ? AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE
            : AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE;

        var status = Open(ToServerPath(path), mask, disposition, CreateOptions.FILE_NON_DIRECTORY_FILE,
            out var handle, out var fileStatus);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            if (IsNotFound(status) && mode is FileMode.Open or FileMode.Truncate)
                throw new FileNotFoundException($"File not found: {path}");
            throw Error(status, path);
        }

        var length = 0L;
        lock (_gate)
        {
            if (_store.GetFileInformation(out var info, handle, FileInformationClass.FileStandardInformation)
                    == NTStatus.STATUS_SUCCESS && info is FileStandardInformation standard)
            {
                length = standard.EndOfFile;
            }
        }

        var position = mode == FileMode.Append ? length : 0L;
        return new SmbFileStream(_store, _gate, handle, access, position, length);
    }

    // --- 元数据 ---

    /// <summary>读取最后写入时间：打开后查询网络打开信息。</summary>
    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        var status = Open(ToServerPath(path), AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE, out var handle, out _);
        if (status != NTStatus.STATUS_SUCCESS)
            throw Error(status, path);

        try
        {
            FileInformation info;
            lock (_gate)
            {
                _store.GetFileInformation(out info, handle, FileInformationClass.FileNetworkOpenInformation);
            }
            if (info is FileNetworkOpenInformation open)
                return open.LastWriteTime ?? DateTime.MinValue;
            return DateTime.MinValue;
        }
        finally
        {
            CloseRaw(handle);
        }
    }

    // --- 移动 / 改名 ---

    /// <summary>用 SMB2 原生重命名移动文件。</summary>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
        => Rename(srcPath, destPath, CreateOptions.FILE_NON_DIRECTORY_FILE);

    /// <summary>用 SMB2 原生重命名移动目录。</summary>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
        => Rename(srcPath, destPath, CreateOptions.FILE_DIRECTORY_FILE);

    private void Rename(UPath srcPath, UPath destPath, CreateOptions options)
    {
        var status = Open(ToServerPath(srcPath), AccessMask.DELETE, CreateDisposition.FILE_OPEN,
            options, out var handle, out _);
        if (status != NTStatus.STATUS_SUCCESS)
            throw Error(status, srcPath);

        try
        {
            var rename = new FileRenameInformationType2
            {
                ReplaceIfExists = true,
                FileName = "\\" + ToServerPath(destPath),
            };
            lock (_gate)
            {
                status = _store.SetFileInformation(handle, rename);
            }
            if (status != NTStatus.STATUS_SUCCESS)
                throw Error(status, destPath);
        }
        finally
        {
            CloseRaw(handle);
        }
    }

    // --- 枚举 ---

    /// <summary>枚举目录：一次查询当前目录，按搜索目标/通配符过滤，必要时递归。</summary>
    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchDepth searchOption, SearchTarget searchTarget)
    {
        foreach (var (name, isDir) in ListDirectory(path))
        {
            if ((searchTarget == SearchTarget.Both) ||
                (searchTarget == SearchTarget.Directory && isDir) ||
                (searchTarget == SearchTarget.File && !isDir))
            {
                if (MatchesPattern(name, searchPattern))
                    yield return path / name;
            }

            if (searchOption == SearchDepth.AllDirectories && isDir)
            {
                foreach (var sub in EnumeratePathsImpl(path / name, searchPattern, searchOption, searchTarget))
                    yield return sub;
            }
        }
    }

    /// <summary>列出目录项；不做通配过滤。</summary>
    private List<(string Name, bool IsDirectory)> ListDirectory(UPath path)
    {
        var status = Open(ToServerPath(path), AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, out var handle, out _);
        if (status == NTStatus.STATUS_OBJECT_PATH_NOT_FOUND || status == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND)
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        if (status != NTStatus.STATUS_SUCCESS)
            throw Error(status, path);

        var result = new List<(string, bool)>();
        try
        {
            while (true)
            {
                NTStatus queryStatus;
                List<QueryDirectoryFileInformation> entries;
                lock (_gate)
                {
                    queryStatus = _store.QueryDirectory(out entries, handle, "*",
                        FileInformationClass.FileDirectoryInformation);
                }

                if (queryStatus != NTStatus.STATUS_SUCCESS &&
                    queryStatus != NTStatus.STATUS_BUFFER_OVERFLOW &&
                    queryStatus != NTStatus.STATUS_NO_MORE_FILES)
                {
                    throw Error(queryStatus, path);
                }

                foreach (var entry in entries.OfType<FileDirectoryInformation>())
                {
                    if (entry.FileName is "." or "..")
                        continue;
                    result.Add((entry.FileName, entry.FileAttributes.HasFlag(SMBLibrary.FileAttributes.Directory)));
                }

                if (queryStatus != NTStatus.STATUS_SUCCESS || entries.Count == 0)
                    break;
            }
        }
        finally
        {
            CloseRaw(handle);
        }

        return result;
    }

    /// <summary>简单通配匹配：* 匹配任意多个字符，? 匹配单个字符（忽略大小写）。</summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>释放时断开共享与登录会话。</summary>
    public override void Dispose()
    {
        try
        {
            _store.Disconnect();
        }
        catch
        {
            // 断开失败不影响进程退出。
        }

        try
        {
            _client.Logoff();
            _client.Disconnect();
        }
        catch
        {
            // 同上。
        }

        base.Dispose();
    }
}

/// <summary>SMB 文件流：按偏移读写 SMB2 句柄，写操作按服务端上限分片。</summary>
internal sealed class SmbFileStream : Stream
{
    private readonly ISMBFileStore _store;
    private readonly object _gate;
    private readonly object _handle;
    private readonly FileAccess _access;
    private readonly bool _canRead;
    private readonly bool _canWrite;
    private long _position;
    private long _length;
    private bool _disposed;

    internal SmbFileStream(ISMBFileStore store, object gate, object handle, FileAccess access, long position, long length)
    {
        _store = store;
        _gate = gate;
        _handle = handle;
        _access = access;
        _canRead = access.HasFlag(FileAccess.Read);
        _canWrite = access.HasFlag(FileAccess.Write);
        _position = position;
        _length = length;
    }

    /// <summary>流不可读时抛出。</summary>
    public override bool CanRead => !_disposed && _canRead;
    /// <summary>流不可定位（SMB 句柄支持偏移，故支持）。</summary>
    public override bool CanSeek => !_disposed;
    /// <summary>流是否可写。</summary>
    public override bool CanWrite => !_disposed && _canWrite;
    /// <summary>文件长度。</summary>
    public override long Length => _length;

    /// <summary>当前偏移；允许越过末尾以支持顺序追加。</summary>
    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    /// <summary>从当前位置读取，最多一次服务端允许的字节数。</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!CanRead)
            throw new NotSupportedException("Stream is not readable.");
        if (count == 0)
            return 0;

        var max = (int)Math.Min(count, (long)_store.MaxReadSize);
        if (max <= 0)
            return 0;

        NTStatus status;
        byte[]? data;
        lock (_gate)
        {
            status = _store.ReadFile(out data, _handle, _position, max);
        }

        if (status == NTStatus.STATUS_END_OF_FILE)
            return 0;
        if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_BUFFER_OVERFLOW)
            throw new IOException($"SMB read failed: {status}");

        var read = data?.Length ?? 0;
        if (read == 0)
            return 0;
        Buffer.BlockCopy(data!, 0, buffer, offset, read);
        _position += read;
        return read;
    }

    /// <summary>在当前位置写入，按服务端 MaxWriteSize 分片。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!CanWrite)
            throw new NotSupportedException("Stream is not writable.");

        var chunkSize = (int)Math.Min(count, (long)_store.MaxWriteSize);
        if (chunkSize <= 0)
            throw new IOException("SMB server reported a zero maximum write size.");

        var written = 0;
        while (written < count)
        {
            var size = Math.Min(chunkSize, count - written);
            var chunk = new byte[size];
            Buffer.BlockCopy(buffer, offset + written, chunk, 0, size);

            NTStatus status;
            int sent;
            lock (_gate)
            {
                status = _store.WriteFile(out sent, _handle, _position, chunk);
            }
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"SMB write failed: {status}");

            _position += sent;
            written += sent;
            if (sent <= 0)
                break;
        }

        _length = Math.Max(_length, _position);
    }

    /// <summary>调整文件长度（截断或补零）。</summary>
    public override void SetLength(long value)
    {
        if (!CanWrite)
            throw new NotSupportedException("Stream is not writable.");

        NTStatus status;
        lock (_gate)
        {
            status = _store.SetFileInformation(_handle, new FileEndOfFileInformation { EndOfFile = value });
        }
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"SMB truncate failed: {status}");

        _length = value;
        if (_position > value)
            _position = value;
    }

    /// <summary>刷新服务端缓冲区。</summary>
    public override void Flush()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            _store.FlushFileBuffers(_handle);
        }
    }

    /// <summary>调整偏移。</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    /// <summary>关闭 SMB 句柄。</summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }
        _disposed = true;

        lock (_gate)
        {
            _store.CloseFile(_handle);
        }

        base.Dispose(disposing);
    }
}
