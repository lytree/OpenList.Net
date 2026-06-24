using System.Net;
using System.Security.Claims;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Services;
using Zio;

namespace OpenList.Server.Hosting;

/// <summary>
/// Hosted service that runs an FTP server backed by the Zio <see cref="MountFileSystem"/>.
/// Disabled by default — enable via <c>FTP:Enable</c>.
/// Mirrors OpenList Go <c>server/ftp</c>.
/// </summary>
public sealed class FtpHostedService : IHostedService
{
    private readonly IFtpServerHost _ftpServerHost;
    private readonly ILogger<FtpHostedService> _logger;
    private readonly FtpConfig _config;

    public FtpHostedService(
        IFtpServerHost ftpServerHost,
        IOptions<AppConfiguration> config,
        ILogger<FtpHostedService> logger)
    {
        _ftpServerHost = ftpServerHost;
        _logger = logger;
        _config = config.Value.FTP;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enable)
        {
            _logger.LogInformation("FTP server disabled (FTP:Enable=false)");
            return;
        }

        _logger.LogInformation("Starting FTP server on {Listen}", _config.Listen);
        await _ftpServerHost.StartAsync(cancellationToken);
        _logger.LogInformation("FTP server started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enable) return;
        _logger.LogInformation("Stopping FTP server");
        await _ftpServerHost.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Parses <c>FTP:Listen</c> into address/port and configures <see cref="FtpServerOptions"/>.
/// </summary>
public static class FtpListenParser
{
    public static (string addr, int port) Parse(string listen)
    {
        if (string.IsNullOrWhiteSpace(listen)) return ("0.0.0.0", 5221);
        var v = listen.TrimStart(':');
        if (int.TryParse(v, out var portOnly)) return ("0.0.0.0", portOnly);
        if (IPEndPoint.TryParse(listen, out var ep)) return (ep.Address.ToString(), ep.Port);
        var parts = listen.Split(':', 2);
        if (parts.Length == 2 && int.TryParse(parts[1], out var p))
        {
            return (IPAddress.TryParse(parts[0], out _) ? parts[0] : "0.0.0.0", p);
        }
        return ("0.0.0.0", 5221);
    }
}

/// <summary>
/// Membership provider that authenticates FTP users against OpenList's <see cref="IUserService"/>.
/// Anonymous login is rejected.
/// </summary>
public sealed class OpenListFtpMembershipProvider : IMembershipProvider
{
    private readonly IUserService _userService;

    public OpenListFtpMembershipProvider(IUserService userService)
    {
        _userService = userService;
    }

    public async Task<MemberValidationResult> ValidateUserAsync(
        string username,
        string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

        var resp = await _userService.GetByNameAsync(username, CancellationToken.None);
        if (resp.Code != 200 || resp.Data is null)
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

        if (resp.Data.Disabled)
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

        if (!UserService.VerifyPassword(password, resp.Data.PasswordHash))
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

        var identity = new ClaimsIdentity("Ftp");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resp.Data.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, resp.Data.Username));
        identity.AddClaim(new Claim(ClaimTypes.Role, resp.Data.Role.ToString("D")));
        var principal = new ClaimsPrincipal(identity);

        return new MemberValidationResult(MemberValidationStatus.Anonymous, principal);
    }
}

/// <summary>
/// Factory that creates a Zio-backed <see cref="IUnixFileSystem"/> for each FTP session.
/// </summary>
public sealed class ZioFtpFileSystemFactory : IFileSystemClassFactory
{
    private readonly ZioMountService _mount;

    public ZioFtpFileSystemFactory(ZioMountService mount)
    {
        _mount = mount;
    }

    public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var fs = new ZioUnixFileSystem(_mount);
        return Task.FromResult<IUnixFileSystem>(fs);
    }
}

/// <summary>
/// Adapter that exposes the Zio <see cref="MountFileSystem"/> as an <see cref="IUnixFileSystem"/>
/// for the FTP server.
/// </summary>
public sealed class ZioUnixFileSystem : IUnixFileSystem
{
    private readonly ZioMountService _mount;
    private readonly ZioUnixDirectoryEntry _root;

    public ZioUnixFileSystem(ZioMountService mount)
    {
        _mount = mount;
        _root = new ZioUnixDirectoryEntry(_mount, UPath.Root, "/");
    }

    public StringComparer FileSystemEntryComparer => StringComparer.OrdinalIgnoreCase;
    public IUnixDirectoryEntry Root => _root;
    public bool SupportsAppend => true;
    public bool SupportsNonEmptyDirectoryDelete => true;

    public Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(
        IUnixDirectoryEntry directoryEntry,
        CancellationToken cancellationToken)
    {
        var zioEntry = (ZioUnixDirectoryEntry)directoryEntry;
        var entries = new List<IUnixFileSystemEntry>();

        if (_mount.Root.DirectoryExists(zioEntry.Path))
        {
            foreach (var child in _mount.Root.EnumeratePaths(zioEntry.Path, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both))
            {
                entries.Add(_mount.Root.FileExists(child)
                    ? new ZioUnixFileEntry(_mount, child, child.GetName())
                    : new ZioUnixDirectoryEntry(_mount, child, child.GetName() + "/"));
            }
        }

        return Task.FromResult<IReadOnlyList<IUnixFileSystemEntry>>(entries);
    }

    public Task<IUnixFileSystemEntry?> GetEntryByNameAsync(
        IUnixDirectoryEntry directoryEntry,
        string name,
        CancellationToken cancellationToken)
    {
        var zioEntry = (ZioUnixDirectoryEntry)directoryEntry;
        var childPath = zioEntry.Path / name;

        if (_mount.Root.FileExists(childPath))
            return Task.FromResult<IUnixFileSystemEntry?>(new ZioUnixFileEntry(_mount, childPath, name));
        if (_mount.Root.DirectoryExists(childPath))
            return Task.FromResult<IUnixFileSystemEntry?>(new ZioUnixDirectoryEntry(_mount, childPath, name + "/"));

        return Task.FromResult<IUnixFileSystemEntry?>(null);
    }

    public Task<Stream> OpenReadAsync(
        IUnixFileEntry fileEntry,
        long startPosition,
        CancellationToken cancellationToken)
    {
        var zioEntry = (ZioUnixFileEntry)fileEntry;
        var stream = _mount.Root.OpenFile(zioEntry.Path, FileMode.Open, FileAccess.Read);
        if (startPosition > 0) stream.Position = startPosition;
        return Task.FromResult(stream);
    }

    public Task<IBackgroundTransfer?> CreateAsync(
        IUnixDirectoryEntry targetDirectory,
        string fileName,
        Stream data,
        CancellationToken cancellationToken)
    {
        var zioDir = (ZioUnixDirectoryEntry)targetDirectory;
        var filePath = zioDir.Path / fileName;

        var dirPath = filePath.GetDirectory();
        if (!_mount.Root.DirectoryExists(dirPath))
            _mount.Root.CreateDirectory(dirPath);

        var fs = _mount.Root.OpenFile(filePath, FileMode.Create, FileAccess.Write);
        return Task.FromResult<IBackgroundTransfer?>(new StreamBackgroundTransfer(data, fs));
    }

    public Task<IBackgroundTransfer?> ReplaceAsync(
        IUnixFileEntry fileEntry,
        Stream data,
        CancellationToken cancellationToken)
    {
        var zioEntry = (ZioUnixFileEntry)fileEntry;
        var fs = _mount.Root.OpenFile(zioEntry.Path, FileMode.Create, FileAccess.Write);
        return Task.FromResult<IBackgroundTransfer?>(new StreamBackgroundTransfer(data, fs));
    }

    public Task<IBackgroundTransfer?> AppendAsync(
        IUnixFileEntry fileEntry,
        long? startPosition,
        Stream data,
        CancellationToken cancellationToken)
    {
        var zioEntry = (ZioUnixFileEntry)fileEntry;
        var fs = _mount.Root.OpenFile(zioEntry.Path, FileMode.Append, FileAccess.Write);
        return Task.FromResult<IBackgroundTransfer?>(new StreamBackgroundTransfer(data, fs));
    }

    public Task<IUnixDirectoryEntry> CreateDirectoryAsync(
        IUnixDirectoryEntry targetDirectory,
        string directoryName,
        CancellationToken cancellationToken)
    {
        var zioDir = (ZioUnixDirectoryEntry)targetDirectory;
        var newPath = zioDir.Path / directoryName;
        _mount.Root.CreateDirectory(newPath);
        return Task.FromResult<IUnixDirectoryEntry>(new ZioUnixDirectoryEntry(_mount, newPath, directoryName + "/"));
    }

    public Task UnlinkAsync(
        IUnixFileSystemEntry entry,
        CancellationToken cancellationToken)
    {
        var zioEntry = (IZioUnixEntry)entry;
        if (_mount.Root.FileExists(zioEntry.Path))
            _mount.Root.DeleteFile(zioEntry.Path);
        else if (_mount.Root.DirectoryExists(zioEntry.Path))
            _mount.Root.DeleteDirectory(zioEntry.Path, true);
        return Task.CompletedTask;
    }

    public Task<IUnixFileSystemEntry> MoveAsync(
        IUnixDirectoryEntry parent,
        IUnixFileSystemEntry source,
        IUnixDirectoryEntry target,
        string fileName,
        CancellationToken cancellationToken)
    {
        var zioSource = (IZioUnixEntry)source;
        var zioTarget = (ZioUnixDirectoryEntry)target;
        var newPath = zioTarget.Path / fileName;

        if (_mount.Root.FileExists(zioSource.Path))
            _mount.Root.MoveFile(zioSource.Path, newPath);
        else if (_mount.Root.DirectoryExists(zioSource.Path))
            _mount.Root.MoveDirectory(zioSource.Path, newPath);

        return Task.FromResult<IUnixFileSystemEntry>(
            _mount.Root.FileExists(newPath)
                ? new ZioUnixFileEntry(_mount, newPath, fileName)
                : new ZioUnixDirectoryEntry(_mount, newPath, fileName + "/"));
    }

    public Task<IUnixFileSystemEntry> SetMacTimeAsync(
        IUnixFileSystemEntry entry,
        DateTimeOffset? modify,
        DateTimeOffset? access,
        DateTimeOffset? create,
        CancellationToken cancellationToken)
    {
        // Zio doesn't expose per-entry timestamp updates uniformly across drivers.
        return Task.FromResult(entry);
    }
}

public interface IZioUnixEntry
{
    UPath Path { get; }
}

public sealed class ZioUnixFileEntry : IUnixFileEntry, IZioUnixEntry
{
    private readonly ZioMountService _mount;
    public UPath Path { get; }

    public ZioUnixFileEntry(ZioMountService mount, UPath path, string name)
    {
        _mount = mount;
        Path = path;
        Name = name;
        var entry = mount.Root.GetFileEntry(path);
        Size = entry.Length;
        LastWriteTime = new DateTimeOffset(entry.LastWriteTime.ToUniversalTime(), TimeSpan.Zero);
        CreatedTime = new DateTimeOffset(entry.CreationTime.ToUniversalTime(), TimeSpan.Zero);
    }

    public string Name { get; }
    public long Size { get; }
    public DateTimeOffset? LastWriteTime { get; }
    public DateTimeOffset? CreatedTime { get; }
    public IUnixPermissions Permissions { get; } = new GenericUnixPermissions(
        new GenericAccessMode(true, true, true),
        new GenericAccessMode(true, false, true),
        new GenericAccessMode(true, false, true));
    public string Owner => "root";
    public string Group => "root";
    public long NumberOfLinks => 1;
}

public sealed class ZioUnixDirectoryEntry : IUnixDirectoryEntry, IZioUnixEntry
{
    private readonly ZioMountService _mount;
    public UPath Path { get; }

    public ZioUnixDirectoryEntry(ZioMountService mount, UPath path, string name)
    {
        _mount = mount;
        Path = path;
        Name = name;
        IsRoot = path == UPath.Root;
    }

    public string Name { get; }
    public DateTimeOffset? LastWriteTime { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CreatedTime { get; } = DateTimeOffset.UtcNow;
    public IUnixPermissions Permissions { get; } = new GenericUnixPermissions(
        new GenericAccessMode(true, true, true),
        new GenericAccessMode(true, false, true),
        new GenericAccessMode(true, false, true));
    public string Owner => "root";
    public string Group => "root";
    public long NumberOfLinks => 1;
    public bool IsDeletable => true;
    public bool IsRoot { get; }
}

/// <summary>
/// Copies from a source stream into a destination file stream as a background transfer.
/// </summary>
public sealed class StreamBackgroundTransfer : IBackgroundTransfer
{
    private readonly Stream _source;
    private readonly Stream _destination;
    private bool _disposed;

    public StreamBackgroundTransfer(Stream source, Stream destination)
    {
        _source = source;
        _destination = destination;
        TransferId = Guid.NewGuid().ToString("N");
    }

    public string TransferId { get; }

    public async Task Start(IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await _source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await _destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Report(total);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _source.Dispose();
        _destination.Dispose();
        _disposed = true;
    }
}
