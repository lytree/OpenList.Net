using Renci.SshNet;
using Renci.SshNet.Sftp;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.FtpSftpSmb;

/// <summary>
/// Zio FileSystem implementation backed by an SFTP server via SSH.NET.
/// </summary>
internal sealed class SftpFileSystem : NetworkFileSystemBase
{
    private readonly SftpClient _client;
    private readonly string _rootPath;

    public SftpFileSystem(SftpClient client, string rootPath)
    {
        _client = client;
        _rootPath = string.IsNullOrEmpty(rootPath) ? "/" : rootPath;
        _client.Connect();
    }

    // --- Path conversion ---

    private string ToRemote(UPath path)
    {
        var relative = path.FullName.TrimStart('/');
        var full = string.IsNullOrEmpty(relative) ? _rootPath : _rootPath.TrimEnd('/') + "/" + relative;
        return full;
    }

    // --- Existence ---

    protected override bool DirectoryExistsImpl(UPath path)
        => _client.Exists(ToRemote(path)) && _client.GetAttributes(ToRemote(path)).IsDirectory;

    protected override bool FileExistsImpl(UPath path)
        => _client.Exists(ToRemote(path)) && _client.GetAttributes(ToRemote(path)).IsRegularFile;

    // --- Directory ops ---

    protected override void CreateDirectoryImpl(UPath path)
        => _client.CreateDirectory(ToRemote(path));

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        if (isRecursive)
        {
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

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        // SFTP doesn't have a direct move-directory; rename works if same filesystem
        _client.RenameFile(ToRemote(srcPath), ToRemote(destPath));
    }

    // --- File ops ---

    protected override void DeleteFileImpl(UPath path)
        => _client.DeleteFile(ToRemote(path));

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
        => _client.RenameFile(ToRemote(srcPath), ToRemote(destPath));

    // --- Open file ---

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var remote = ToRemote(path);

        if (access == FileAccess.Read || (access == FileAccess.ReadWrite && mode == FileMode.Open))
        {
            return _client.OpenRead(remote);
        }

        return _client.OpenWrite(remote);
    }

    // --- Metadata ---

    protected override long GetFileLengthImpl(UPath path)
        => _client.GetAttributes(ToRemote(path)).Size;

    protected override DateTime GetLastWriteTimeImpl(UPath path)
        => _client.GetAttributes(ToRemote(path)).LastWriteTime;

    protected override DateTime GetCreationTimeImpl(UPath path)
        => _client.GetAttributes(ToRemote(path)).LastWriteTime;

    // --- Enumeration ---

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        foreach (var item in _client.ListDirectory(ToRemote(path)))
        {
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
            _client.Disconnect();
            _client.Dispose();
        }
        base.Dispose(disposing);
    }
}
