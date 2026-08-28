using Renci.SshNet;
using Renci.SshNet.Sftp;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// 基于 SSH.NET 的 SFTP 服务器的 Zio 文件系统实现。
/// </summary>
internal sealed class SftpFileSystem : NetworkFileSystemBase
{
    private readonly SftpClient _client;
    private readonly string _rootPath;

    /// <summary>构造：保存客户端与根路径，并立即建立 SSH/SFTP 连接。</summary>
    public SftpFileSystem(SftpClient client, string rootPath)
    {
        _client = client;
        _rootPath = string.IsNullOrEmpty(rootPath) ? "/" : rootPath;
        _client.Connect();
    }

    // --- 路径转换 ---

    /// <summary>把 Zio 路径拼接到 SFTP 根路径下，得到远程完整路径。</summary>
    private string ToRemote(UPath path)
    {
        var relative = path.FullName.TrimStart('/');
        var full = string.IsNullOrEmpty(relative) ? _rootPath : _rootPath.TrimEnd('/') + "/" + relative;
        return full;
    }

    // --- 存在性 ---

    /// <summary>目录存在 = 路径存在且属性为目录。</summary>
    protected override bool DirectoryExistsImpl(UPath path)
        => _client.Exists(ToRemote(path)) && _client.GetAttributes(ToRemote(path)).IsDirectory;

    /// <summary>文件存在 = 路径存在且属性为普通文件。</summary>
    protected override bool FileExistsImpl(UPath path)
        => _client.Exists(ToRemote(path)) && _client.GetAttributes(ToRemote(path)).IsRegularFile;

    // --- 目录操作 ---

    protected override void CreateDirectoryImpl(UPath path)
        => _client.CreateDirectory(ToRemote(path));

    /// <summary>递归删除目录：先递归删除子项，再删除目录本身。</summary>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        if (isRecursive)
        {
            // 逐项处理，目录递归删除、文件直接删除，跳过 "." 与 ".."。
            foreach (var item in _client.ListDirectory(ToRemote(path)))
            {
                if (item.Name == "." || item.Name == "..") continue;
                var childPath = path / item.Name;
                if (item.IsDirectory)
                    DeleteDirectory(childPath, isRecursive: true);
                else
                    _client.DeleteFile(ToRemote(childPath));
            }
        }
        _client.DeleteDirectory(ToRemote(path));
    }

    /// <summary>移动目录：SFTP 无独立移动目录命令，同文件系统内可先重命名。</summary>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        // SFTP 没有直接移动目录的命令；同一文件系统内可用改名实现。
        _client.RenameFile(ToRemote(srcPath), ToRemote(destPath));
    }

    // --- 文件操作 ---

    protected override void DeleteFileImpl(UPath path)
        => _client.DeleteFile(ToRemote(path));

    /// <summary>移动文件 = SFTP RenameFile。</summary>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
        => _client.RenameFile(ToRemote(srcPath), ToRemote(destPath));

    // --- 打开文件 ---

    /// <summary>以读模式或已有文件的读写模式打开读取流，否则返回写流。</summary>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var remote = ToRemote(path);

        if (access == FileAccess.Read || (access == FileAccess.ReadWrite && mode == FileMode.Open))
        {
            return _client.OpenRead(remote);
        }

        return _client.OpenWrite(remote);
    }

    // --- 元数据 ---

    protected override long GetFileLengthImpl(UPath path)
        => _client.GetAttributes(ToRemote(path)).Size;

    protected override DateTime GetLastWriteTimeImpl(UPath path)
        => _client.GetAttributes(ToRemote(path)).LastWriteTime;

    protected override DateTime GetCreationTimeImpl(UPath path)
        => _client.GetAttributes(ToRemote(path)).LastWriteTime;

    // --- 枚举 ---

    /// <summary>枚举路径：列出远程目录内容，按搜索目标/通配符过滤，必要时递归。</summary>
    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        foreach (var item in _client.ListDirectory(ToRemote(path)))
        {
            // 跳过目录列表中的 "." 与 ".." 相对项。
            if (item.Name == "." || item.Name == "..") continue;
            var childPath = path / item.Name;
            var isDir = item.IsDirectory;

            if ((searchTarget == SearchTarget.Both) ||
                (searchTarget == SearchTarget.Directory && isDir) ||
                (searchTarget == SearchTarget.File && !isDir))
            {
                if (MatchesPattern(item.Name, searchPattern))
                    yield return childPath;
            }

            // 需要递归时对目录项逐层下钻。
            if (searchOption == SearchOption.AllDirectories && isDir)
            {
                foreach (var sub in EnumeratePathsImpl(childPath, searchPattern, searchOption, searchTarget))
                    yield return sub;
            }
        }
    }

    /// <summary>简单通配匹配：* 匹配任意多个字符，? 匹配单个字符（忽略大小写）。</summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>释放时断开并销毁 SFTP 客户端连接。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Disconnect();
            _client.Dispose();
        }
        base.Dispose(disposing);
    }
}
