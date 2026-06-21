using System.Security.Claims;
using FubarDev.FtpServer;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.BackgroundTransfer;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.FtpServer;

// ── Entry types ────────────────────────────────────────────────────────

/// <summary>Base entry wrapping an IObj from OpenList's file system.</summary>
internal abstract class FtpEntryBase : IUnixFileSystemEntry
{
    public string Name { get; }
    public string FullPath { get; }
    public DateTimeOffset? LastWriteTime { get; }
    public DateTimeOffset? CreatedTime { get; }
    public long NumberOfLinks => 1;
    public IUnixPermissions Permissions { get; }
    public string Owner { get; }
    public string Group => "openlist";

    protected FtpEntryBase(string name, string fullPath, DateTime modTime, string owner, bool isDir)
    {
        Name = name;
        FullPath = fullPath;
        LastWriteTime = modTime == default ? null : new DateTimeOffset(modTime);
        CreatedTime = LastWriteTime;
        Owner = owner;
        // rwxr-xr-x for dirs, rw-r--r-- for files
        Permissions = new GenericUnixPermissions(isDir ? "755" : "644");
    }
}

internal sealed class FtpDirEntry : FtpEntryBase, IUnixDirectoryEntry
{
    public bool IsRoot { get; }
    public bool IsDeletable => !IsRoot;

    public FtpDirEntry(string name, string fullPath, DateTime modTime, string owner, bool isRoot = false)
        : base(name, fullPath, modTime, owner, true)
    {
        IsRoot = isRoot;
    }
}

internal sealed class FtpFileEntry : FtpEntryBase, IUnixFileEntry
{
    public long Size { get; }

    public FtpFileEntry(string name, string fullPath, DateTime modTime, string owner, long size)
        : base(name, fullPath, modTime, owner, false)
    {
        Size = size;
    }
}

/// <summary>Simple permissions implementation based on a mode string.</summary>
internal sealed class GenericUnixPermissions : IUnixPermissions
{
    private readonly GenericAccessMode _user;
    private readonly GenericAccessMode _group;
    private readonly GenericAccessMode _other;

    /// <summary>Parse Unix mode string like "755" or "644".</summary>
    public GenericUnixPermissions(string mode)
    {
        int m = mode.Length >= 3 ? int.Parse(mode.Substring(0, 3)) : 755;
        int u = (m / 100) % 10;
        int g = (m / 10) % 10;
        int o = m % 10;
        _user = new GenericAccessMode(u);
        _group = new GenericAccessMode(g);
        _other = new GenericAccessMode(o);
    }

    public IAccessMode User => _user;
    public IAccessMode Group => _group;
    public IAccessMode Other => _other;
}

internal sealed class GenericAccessMode : IAccessMode
{
    private readonly int _bits;
    public GenericAccessMode(int bits) { _bits = bits; }
    public bool Read => (_bits & 4) != 0;
    public bool Write => (_bits & 2) != 0;
    public bool Execute => (_bits & 1) != 0;
}

// ── File system factory ────────────────────────────────────────────────

/// <summary>
/// Creates an OpenListFtpFileSystem per FTP login session.
/// Go: server/ftp.go AuthUser() → ftp.NewAferoAdapter(ctx)
/// </summary>
public class OpenListFtpFileSystemFactory : IFileSystemClassFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenListFtpFileSystem> _logger;

    public OpenListFtpFileSystemFactory(IServiceScopeFactory scopeFactory, ILogger<OpenListFtpFileSystem> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var principal = accountInformation.FtpUser;
        var basePath = principal.FindFirst("openlist:basePath")?.Value ?? "/";
        var username = principal.FindFirst(ClaimTypes.Name)?.Value ?? "unknown";
        var permStr = principal.FindFirst("openlist:permission")?.Value ?? "0";
        int.TryParse(permStr, out var permission);

        var scope = _scopeFactory.CreateScope();
        var fs = scope.ServiceProvider.GetRequiredService<IFileSystemService>();

        IUnixFileSystem fileSystem = new OpenListFtpFileSystem(scope, fs, basePath, username, permission, _logger);
        return Task.FromResult(fileSystem);
    }
}

// ── File system implementation ─────────────────────────────────────────

/// <summary>
/// Bridges FubarDev's IUnixFileSystem with OpenList's IFileSystemService.
/// All FTP file operations (LIST, RETR, STOR, MKDIR, DELE, RENAME) go through here.
/// Go: server/ftp/afero.go + server/ftp/fsread.go + server/ftp/fsmanage.go + server/ftp/fsup.go
/// </summary>
public class OpenListFtpFileSystem : IUnixFileSystem, IDisposable
{
    private readonly IServiceScope _scope;
    private readonly IFileSystemService _fs;
    private readonly string _basePath;
    private readonly string _username;
    private readonly int _permission;
    private readonly ILogger _logger;
    private readonly FtpDirEntry _root;

    public bool SupportsAppend => false;
    public bool SupportsNonEmptyDirectoryDelete => true;
    public StringComparer FileSystemEntryComparer => StringComparer.OrdinalIgnoreCase;
    public IUnixDirectoryEntry Root => _root;

    private bool CanManage => ((UserPermission)_permission & UserPermission.CanFTPManage) != 0;

    public OpenListFtpFileSystem(
        IServiceScope scope, IFileSystemService fs,
        string basePath, string username, int permission, ILogger logger)
    {
        _scope = scope;
        _fs = fs;
        _basePath = basePath.TrimEnd('/') == "" ? "/" : basePath.TrimEnd('/');
        _username = username;
        _permission = permission;
        _logger = logger;
        _root = new FtpDirEntry("", _basePath, DateTime.UtcNow, username, isRoot: true);
    }

    /// <summary>Resolve FTP-relative path to actual storage path.</summary>
    private string ResolvePath(string ftpPath)
    {
        var clean = (ftpPath ?? "").Trim('/');
        if (string.IsNullOrEmpty(clean)) return _basePath;
        if (_basePath == "/") return "/" + clean;
        return _basePath + "/" + clean;
    }

    private string ResolvePath(FtpEntryBase entry) => entry.FullPath;

    private string GetRelativeName(string fullPath)
    {
        if (_basePath == "/") return fullPath.TrimStart('/');
        if (fullPath.StartsWith(_basePath + "/")) return fullPath[(_basePath.Length + 1)..];
        if (fullPath == _basePath) return "";
        return Path.GetFileName(fullPath);
    }

    // ── GetEntriesAsync (LIST / MLSD) ──
    public async Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(
        IUnixDirectoryEntry directoryEntry, CancellationToken cancellationToken)
    {
        var path = ((FtpEntryBase)directoryEntry).FullPath;
        try
        {
            var objs = await _fs.ListAsync(path, ct: cancellationToken);
            var entries = new List<IUnixFileSystemEntry>(objs.Count);
            foreach (var obj in objs)
            {
                var name = obj.GetName();
                var fullPath = path == "/" ? "/" + name : path + "/" + name;
                if (obj.IsDir())
                    entries.Add(new FtpDirEntry(name, fullPath, obj.GetModTime(), _username));
                else
                    entries.Add(new FtpFileEntry(name, fullPath, obj.GetModTime(), _username, obj.GetSize()));
            }
            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTP LIST failed for {Path}", path);
            return Array.Empty<IUnixFileSystemEntry>();
        }
    }

    // ── GetEntryByNameAsync (STAT / single entry lookup) ──
    public async Task<IUnixFileSystemEntry?> GetEntryByNameAsync(
        IUnixDirectoryEntry directoryEntry, string name, CancellationToken cancellationToken)
    {
        var parentPath = ((FtpEntryBase)directoryEntry).FullPath;
        var fullPath = parentPath == "/" ? "/" + name : parentPath + "/" + name;
        try
        {
            var obj = await _fs.GetAsync(fullPath, cancellationToken);
            if (obj == null) return null;
            if (obj.IsDir())
                return new FtpDirEntry(name, fullPath, obj.GetModTime(), _username);
            return new FtpFileEntry(name, fullPath, obj.GetModTime(), _username, obj.GetSize());
        }
        catch
        {
            return null;
        }
    }

    // ── MoveAsync (RENAME / MOVE) ──
    public async Task<IUnixFileSystemEntry> MoveAsync(
        IUnixDirectoryEntry parent, IUnixFileSystemEntry source,
        IUnixDirectoryEntry target, string fileName, CancellationToken cancellationToken)
    {
        if (!CanManage) throw new UnauthorizedAccessException("FTP manage permission required");

        var srcPath = ((FtpEntryBase)source).FullPath;
        var dstDirPath = ((FtpEntryBase)target).FullPath;

        // If same directory → rename
        var srcDir = Path.GetDirectoryName(srcPath)?.Replace('\\', '/') ?? "/";
        if (srcDir == dstDirPath)
        {
            var newName = fileName;
            await _fs.RenameAsync(srcPath, newName, cancellationToken);
        }
        else
        {
            await _fs.MoveAsync(srcPath, dstDirPath, cancellationToken);
            if (fileName != Path.GetFileName(srcPath))
            {
                var newPath = dstDirPath == "/" ? "/" + fileName : dstDirPath + "/" + fileName;
                await _fs.RenameAsync(newPath, fileName, cancellationToken);
            }
        }

        var finalPath = dstDirPath == "/" ? "/" + fileName : dstDirPath + "/" + fileName;
        var obj = await _fs.GetAsync(finalPath, cancellationToken);
        if (obj?.IsDir() == true)
            return new FtpDirEntry(fileName, finalPath, obj.GetModTime(), _username);
        return new FtpFileEntry(fileName, finalPath, obj?.GetModTime() ?? DateTime.UtcNow, _username, obj?.GetSize() ?? 0);
    }

    // ── UnlinkAsync (DELE / RMD) ──
    public async Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken cancellationToken)
    {
        if (!CanManage) throw new UnauthorizedAccessException("FTP manage permission required");
        await _fs.RemoveAsync(((FtpEntryBase)entry).FullPath, cancellationToken);
    }

    // ── CreateDirectoryAsync (MKD) ──
    public async Task<IUnixDirectoryEntry> CreateDirectoryAsync(
        IUnixDirectoryEntry targetDirectory, string directoryName, CancellationToken cancellationToken)
    {
        if (!CanManage) throw new UnauthorizedAccessException("FTP manage permission required");

        var parentPath = ((FtpEntryBase)targetDirectory).FullPath;
        var fullPath = parentPath == "/" ? "/" + directoryName : parentPath + "/" + directoryName;
        await _fs.MakeDirAsync(fullPath, cancellationToken);
        return new FtpDirEntry(directoryName, fullPath, DateTime.UtcNow, _username);
    }

    // ── OpenReadAsync (RETR) ──
    public async Task<Stream> OpenReadAsync(
        IUnixFileEntry fileEntry, long startPosition, CancellationToken cancellationToken)
    {
        var path = ((FtpEntryBase)fileEntry).FullPath;
        var link = await _fs.LinkAsync(path, new LinkArgs { IP = "", Header = null! }, cancellationToken);
        if (link == null || string.IsNullOrEmpty(link.URL))
            throw new FileNotFoundException("File not found", path);

        var http = new HttpClient();
        var response = await http.GetAsync(link.URL, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (startPosition > 0 && stream.CanSeek)
            stream.Position = startPosition;

        return stream;
    }

    // ── CreateAsync (STOR - new file) ──
    public async Task<IBackgroundTransfer?> CreateAsync(
        IUnixDirectoryEntry targetDirectory, string fileName, Stream data, CancellationToken cancellationToken)
    {
        if (!CanManage) throw new UnauthorizedAccessException("FTP manage permission required");

        var parentPath = ((FtpEntryBase)targetDirectory).FullPath;
        await UploadStreamAsync(parentPath, fileName, data, cancellationToken);
        return null;
    }

    // ── ReplaceAsync (STOR - overwrite) ──
    public async Task<IBackgroundTransfer?> ReplaceAsync(
        IUnixFileEntry fileEntry, Stream data, CancellationToken cancellationToken)
    {
        if (!CanManage) throw new UnauthorizedAccessException("FTP manage permission required");

        var path = ((FtpEntryBase)fileEntry).FullPath;
        var dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "/";
        var name = Path.GetFileName(path);

        // Remove existing then upload
        try { await _fs.RemoveAsync(path, cancellationToken); } catch { /* ignore */ }
        await UploadStreamAsync(dir, name, data, cancellationToken);
        return null;
    }

    // ── AppendAsync (APPE) ──
    public Task<IBackgroundTransfer?> AppendAsync(
        IUnixFileEntry fileEntry, long? startPosition, Stream data, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Append is not supported");
    }

    // ── SetMacTimeAsync (MFMT) ──
    public Task<IUnixFileSystemEntry> SetMacTimeAsync(
        IUnixFileSystemEntry entry, DateTimeOffset? modify, DateTimeOffset? access, DateTimeOffset? create,
        CancellationToken cancellationToken)
    {
        // OpenList doesn't support changing timestamps, return as-is
        return Task.FromResult(entry);
    }

    /// <summary>Upload a stream as a file using IFileSystemService.PutAsync.</summary>
    private async Task UploadStreamAsync(string dirPath, string fileName, Stream data, CancellationToken ct)
    {
        // Copy to temp file first since PutAsync needs a FileStreamer
        var tempPath = Path.Combine(Path.GetTempPath(), $"ftp_upload_{Guid.NewGuid():N}");
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await data.CopyToAsync(fs, ct);
            }

            var fi = new FileInfo(tempPath);
            var obj = new ObjItem
            {
                Name = fileName,
                Size = fi.Length,
                Modified = DateTime.UtcNow,
            };
            var fileStreamer = new Core.Drivers.FileStreamer
            {
                Obj = obj,
                Stream = File.OpenRead(tempPath),
            };

            await _fs.PutAsync(dirPath, fileStreamer, _ => { }, ct);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _scope.Dispose();
    }
}
