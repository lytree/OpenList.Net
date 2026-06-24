using System.Net;
using FluentFTP;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// Zio FileSystem implementation backed by an FTP server via FluentFTP.
/// </summary>
internal sealed class FtpFileSystem : NetworkFileSystemBase
{
    private readonly AsyncFtpClient _client;
    private readonly string _rootPath;

    public FtpFileSystem(AsyncFtpClient client, string rootPath)
    {
        _client = client;
        _rootPath = string.IsNullOrEmpty(rootPath) ? "/" : rootPath;
        _client.Connect().GetAwaiter().GetResult();
    }

    // --- Path conversion ---

    private string ToRemote(UPath path)
    {
        var relative = path.FullName.TrimStart('/');
        var full = string.IsNullOrEmpty(relative) ? _rootPath : _rootPath.TrimEnd('/') + "/" + relative;
        return full;
    }

    private static UPath FromRemoteName(string name, UPath parent)
    {
        return parent / name;
    }

    // --- Existence ---

    protected override bool DirectoryExistsImpl(UPath path)
        => _client.DirectoryExists(ToRemote(path)).GetAwaiter().GetResult();

    protected override bool FileExistsImpl(UPath path)
        => _client.FileExists(ToRemote(path)).GetAwaiter().GetResult();

    // --- Directory ops ---

    protected override void CreateDirectoryImpl(UPath path)
        => _client.CreateDirectory(ToRemote(path)).GetAwaiter().GetResult();

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
        => _client.DeleteDirectory(ToRemote(path), isRecursive ? FtpListOption.Recursive : FtpListOption.Auto).GetAwaiter().GetResult();

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
        => _client.MoveDirectory(ToRemote(srcPath), ToRemote(destPath), FtpRemoteExists.Overwrite).GetAwaiter().GetResult();

    // --- File ops ---

    protected override void DeleteFileImpl(UPath path)
        => _client.DeleteFile(ToRemote(path)).GetAwaiter().GetResult();

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
        => _client.MoveFile(ToRemote(srcPath), ToRemote(destPath), FtpRemoteExists.Overwrite).GetAwaiter().GetResult();

    // --- Open file ---

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var remote = ToRemote(path);

        if (access == FileAccess.Read || (access == FileAccess.ReadWrite && mode == FileMode.Open))
        {
            return _client.OpenRead(remote).GetAwaiter().GetResult();
        }

        return _client.OpenWrite(remote).GetAwaiter().GetResult();
    }

    // --- Metadata ---

    protected override long GetFileLengthImpl(UPath path)
        => _client.GetFileSize(ToRemote(path)).GetAwaiter().GetResult();

    protected override DateTime GetLastWriteTimeImpl(UPath path)
        => _client.GetModifiedTime(ToRemote(path)).GetAwaiter().GetResult();

    protected override DateTime GetCreationTimeImpl(UPath path) => GetLastWriteTimeImpl(path);

    // --- Enumeration ---

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

            if (searchOption == SearchOption.AllDirectories && isDir)
            {
                foreach (var sub in EnumeratePathsImpl(childPath, searchPattern, searchOption, searchTarget))
                    yield return sub;
            }
        }
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

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
