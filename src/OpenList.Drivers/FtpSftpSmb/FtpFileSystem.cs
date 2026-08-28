using System.Net;
using FluentFTP;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// 基于 FluentFTP 的 FTP 服务器的 Zio 文件系统实现。
/// </summary>
internal sealed class FtpFileSystem : NetworkFileSystemBase
{
    private readonly AsyncFtpClient _client;
    private readonly string _rootPath;

    /// <summary>构造：保存客户端与根路径，并立即建立连接。</summary>
    public FtpFileSystem(AsyncFtpClient client, string rootPath)
    {
        _client = client;
        _rootPath = string.IsNullOrEmpty(rootPath) ? "/" : rootPath;
        _client.Connect().GetAwaiter().GetResult();
    }

    // --- 路径转换 ---

    /// <summary>把 Zio 路径拼接到 FTP 根路径下，得到远程完整路径。</summary>
    private string ToRemote(UPath path)
    {
        var relative = path.FullName.TrimStart('/');
        var full = string.IsNullOrEmpty(relative) ? _rootPath : _rootPath.TrimEnd('/') + "/" + relative;
        return full;
    }

    /// <summary>把远程子项名称挂接到父路径下，得到子项的 Zio 路径。</summary>
    private static UPath FromRemoteName(string name, UPath parent)
    {
        return parent / name;
    }

    // --- 存在性 ---

    protected override bool DirectoryExistsImpl(UPath path)
        => _client.DirectoryExists(ToRemote(path)).GetAwaiter().GetResult();

    protected override bool FileExistsImpl(UPath path)
        => _client.FileExists(ToRemote(path)).GetAwaiter().GetResult();

    // --- 目录操作 ---

    protected override void CreateDirectoryImpl(UPath path)
        => _client.CreateDirectory(ToRemote(path)).GetAwaiter().GetResult();

    /// <summary>删除目录：递归时附带 Recursive 列表选项，否则自动模式。</summary>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
        => _client.DeleteDirectory(ToRemote(path), isRecursive ? FtpListOption.Recursive : FtpListOption.Auto).GetAwaiter().GetResult();

    /// <summary>移动目录 = FTP MoveDirectory（目标存在时覆盖）。</summary>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
        => _client.MoveDirectory(ToRemote(srcPath), ToRemote(destPath), FtpRemoteExists.Overwrite).GetAwaiter().GetResult();

    // --- 文件操作 ---

    protected override void DeleteFileImpl(UPath path)
        => _client.DeleteFile(ToRemote(path)).GetAwaiter().GetResult();

    /// <summary>移动文件 = FTP MoveFile（目标存在时覆盖）。</summary>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
        => _client.MoveFile(ToRemote(srcPath), ToRemote(destPath), FtpRemoteExists.Overwrite).GetAwaiter().GetResult();

    // --- 打开文件 ---

    /// <summary>以读模式或已有文件的读写模式打开读取流，否则返回写流。</summary>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var remote = ToRemote(path);

        if (access == FileAccess.Read || (access == FileAccess.ReadWrite && mode == FileMode.Open))
        {
            return _client.OpenRead(remote).GetAwaiter().GetResult();
        }

        return _client.OpenWrite(remote).GetAwaiter().GetResult();
    }

    // --- 元数据 ---

    protected override long GetFileLengthImpl(UPath path)
        => _client.GetFileSize(ToRemote(path)).GetAwaiter().GetResult();

    protected override DateTime GetLastWriteTimeImpl(UPath path)
        => _client.GetModifiedTime(ToRemote(path)).GetAwaiter().GetResult();

    protected override DateTime GetCreationTimeImpl(UPath path) => GetLastWriteTimeImpl(path);

    // --- 枚举 ---

    /// <summary>枚举路径：列出远程目录内容，按搜索目标/通配符过滤，必要时递归。</summary>
    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        var items = _client.GetListing(ToRemote(path)).GetAwaiter().GetResult();

        foreach (var item in items)
        {
            var childPath = path / item.Name;
            var isDir = item.Type == FtpObjectType.Directory;

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

    /// <summary>释放时断开并销毁 FTP 客户端连接。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Disconnect().GetAwaiter().GetResult();
            _client.Dispose();
        }
        base.Dispose(disposing);
    }
}
