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
/// 托管服务，运行一个底层由 Zio <see cref="MountFileSystem"/> 支撑的 FTP 服务器。
/// 默认关闭 —— 通过 <c>FTP:Enable</c> 开启。对应 OpenList Go <c>server/ftp</c>。
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

    /// <summary>启动 FTP 服务器；未启用时仅记录日志并直接返回。</summary>
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

    /// <summary>停止 FTP 服务器（仅当启用时执行）。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enable) return;
        _logger.LogInformation("Stopping FTP server");
        await _ftpServerHost.StopAsync(cancellationToken);
    }
}

/// <summary>
/// 解析 <c>FTP:Listen</c> 为地址/端口，并据此配置 <see cref="FtpServerOptions"/>。
/// 支持仅端口、IP:端口的多种写法，非法输入时回退到默认 0.0.0.0:5221。
/// </summary>
public static class FtpListenParser
{
    /// <summary>解析监听字符串，返回 (地址, 端口)。</summary>
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
/// Membership 提供者：基于 OpenList 的 <see cref="IUserService"/> 校验 FTP 用户。
/// 匿名登录一律拒绝。
/// </summary>
public sealed class OpenListFtpMembershipProvider : IMembershipProvider
{
    private readonly IUserService _userService;

    public OpenListFtpMembershipProvider(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>
    /// 按用户名+密码校验 FTP 登录：依次校验用户存在、未禁用、密码匹配，
    /// 校验通过后构造带 id/用户名/角色声明的 ClaimsPrincipal。
    /// </summary>
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

        // 为每个 FTP 会话构造 OpenList 用户身份（含角色声明）。
        var identity = new ClaimsIdentity("Ftp");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resp.Data.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, resp.Data.Username));
        identity.AddClaim(new Claim(ClaimTypes.Role, resp.Data.Role.ToString("D")));
        var principal = new ClaimsPrincipal(identity);

        return new MemberValidationResult(MemberValidationStatus.Anonymous, principal);
    }
}

/// <summary>
/// 为每个 FTP 会话创建基于 Zio 的 <see cref="IUnixFileSystem"/> 的工厂。
/// </summary>
public sealed class ZioFtpFileSystemFactory : IFileSystemClassFactory
{
    private readonly ZioMountService _mount;

    public ZioFtpFileSystemFactory(ZioMountService mount)
    {
        _mount = mount;
    }

    /// <summary>为给定账户创建一个 <see cref="ZioUnixFileSystem"/> 实例。</summary>
    public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var fs = new ZioUnixFileSystem(_mount);
        return Task.FromResult<IUnixFileSystem>(fs);
    }
}

/// <summary>
/// 适配器：将 Zio <see cref="MountFileSystem"/> 暴露为 FTP 服务器所需的
/// <see cref="IUnixFileSystem"/>。
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

        // 遍历目录下的文件与子目录，统一包装为对应的 Unix 条目。
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

    /// <summary>按名称在目录下查找子项（文件/目录），不存在时返回 null。</summary>
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

    /// <summary>以只读方式打开文件（支持从指定偏移开始读取），供 FTP 下载使用。</summary>
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

    /// <summary>在目标目录创建新文件（必要时先建父目录），返回后台传输会话用于写入。</summary>
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

    /// <summary>以覆盖方式替换文件内容，返回后台传输会话。</summary>
    public Task<IBackgroundTransfer?> ReplaceAsync(
        IUnixFileEntry fileEntry,
        Stream data,
        CancellationToken cancellationToken)
    {
        var zioEntry = (ZioUnixFileEntry)fileEntry;
        var fs = _mount.Root.OpenFile(zioEntry.Path, FileMode.Create, FileAccess.Write);
        return Task.FromResult<IBackgroundTransfer?>(new StreamBackgroundTransfer(data, fs));
    }

    /// <summary>以追加方式写入文件（支持续传），返回后台传输会话。</summary>
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

    /// <summary>在目标目录下创建子目录并返回对应条目。</summary>
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

    /// <summary>删除文件或（递归）删除目录。</summary>
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

    /// <summary>将文件/目录移动（重命名）到目标目录下。</summary>
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

    /// <summary>设置条目时间戳（Zio 各驱动对逐条目时间戳的更新支持并不统一，故为空操作）。</summary>
    public Task<IUnixFileSystemEntry> SetMacTimeAsync(
        IUnixFileSystemEntry entry,
        DateTimeOffset? modify,
        DateTimeOffset? access,
        DateTimeOffset? create,
        CancellationToken cancellationToken)
    {
        // Zio 不提供跨驱动一致的逐条目时间戳更新接口。
        return Task.FromResult(entry);
    }
}

/// <summary>统一暴露 Zio 路径，便于 FTP 条目与挂载文件系统之间互转。</summary>
public interface IZioUnixEntry
{
    UPath Path { get; }
}

/// <summary>FTP 会话中的文件条目，映射到 Zio 挂载文件系统的文件。</summary>
public sealed class ZioUnixFileEntry : IUnixFileEntry, IZioUnixEntry
{
    private readonly ZioMountService _mount;
    public UPath Path { get; }

    /// <summary>从 Zio 文件条目读取大小与时间信息，构造对应的 FTP 文件条目。</summary>
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

/// <summary>FTP 会话中的目录条目，映射到 Zio 挂载文件系统的目录。</summary>
public sealed class ZioUnixDirectoryEntry : IUnixDirectoryEntry, IZioUnixEntry
{
    private readonly ZioMountService _mount;
    public UPath Path { get; }

    /// <summary>构造目录条目；根路径（UPath.Root）被标记为 IsRoot。</summary>
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
/// 将源流拷贝到目标文件流，以后台传输的形式执行（供 FTP 上传/下载复用）。
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

    /// <summary>分块拷贝数据并上报进度；取消时抛出 OperationCanceledException。</summary>
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

    /// <summary>释放源与目标流。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _source.Dispose();
        _destination.Dispose();
        _disposed = true;
    }
}
