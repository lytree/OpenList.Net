using System.Net;
using System.Security.Claims;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.Features;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.Generic;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Services;
using OpenList.Vfs;

namespace OpenList.Server.Hosting;

/// <summary>
/// 托管服务，运行一个底层由 <see cref="MountFileSystem"/> 支撑的 FTP 服务器。
/// 默认关闭 —— 通过 <c>FTP:Enable</c> 开启。对应 OpenList Go <c>server/ftp</c>。
/// </summary>
public sealed class FtpHostedService : IHostedService
{
    private readonly IFtpServerHost _ftpServerHost;
    private readonly ILogger<FtpHostedService> _logger;
    private readonly FtpConfig _config;
    private readonly FtpServerSettingsSource _settings;

    public FtpHostedService(
        IFtpServerHost ftpServerHost,
        IOptions<AppConfiguration> config,
        FtpServerSettingsSource settings,
        ILogger<FtpHostedService> logger)
    {
        _ftpServerHost = ftpServerHost;
        _logger = logger;
        _config = config.Value.FTP;
        _settings = settings;
    }

    /// <summary>启动 FTP 服务器；未启用时仅记录日志并直接返回。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enable)
        {
            _logger.LogInformation("FTP server disabled (FTP:Enable=false)");
            return;
        }

        // 预热设置快照：配置错误（端口段/证书/公网地址）在启动日志里就能看到，而不是等首个客户端。
        _ = _settings.Get();

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
    private readonly FtpServerSettingsSource _settings;
    private readonly IFtpConnectionAccessor _connectionAccessor;

    public OpenListFtpMembershipProvider(
        IUserService userService,
        FtpServerSettingsSource settings,
        IFtpConnectionAccessor connectionAccessor)
    {
        _userService = userService;
        _settings = settings;
        _connectionAccessor = connectionAccessor;
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

        // 强制 TLS（ftp_mandatory_tls）：控制通道未加密时拒绝登录 —— 账号密码和文件内容都在裸奔。
        // 注意：不能用 IFtpConnection.IsSecure —— FubarDev 把它实现成永远抛 NotSupportedException
        // 的废弃属性（消息还叫 "Access to irrelevant leaking abstraction"），异常会让 PASS 回 502。
        // 判定用 ISecureConnectionFeature：AUTH TLS 握手完成后库会设置 CloseEncryptedControlStream；
        // 隐式 TLS 模式下控制连接生来就是 TLS，直接放行。
        // 强制 TLS（ftp_mandatory_tls）：控制通道未加密时拒绝登录 —— 账号密码和文件内容都在裸奔。
        //
        // 为什么这么绕：FubarDev 3.1.2 没有公开的「控制通道是否已加密」信号 ——
        // IFtpConnection.IsSecure 被实现成永远抛 NotSupportedException 的废弃属性
        // （异常会让 PASS 回 502 "Access to irrelevant leaking abstraction"）；
        // ISecureConnectionFeature.CloseEncryptedControlStream 默认也是非 null 的 no-op。
        // 可用的判定：AUTH TLS 握手完成后，库（TlsEnableServerCommandHandler）会把
        // CloseEncryptedControlStream 从「无捕获的静态 lambda（委托 Target == null）」
        // 换成「捕获了 feature 的闭包（Target != null）」。隐式 TLS 模式下控制连接
        // 生来就是 TLS，直接放行。两个 lambda 形态随 FubarDev 3.1.2 锁定于
        // Directory.Packages.props，升级版本时这里要复核。
        var ftp = _settings.Get();
        if (ftp.MandatoryTls && !ftp.ImplicitTls)
        {
            var closeHook = _connectionAccessor.FtpConnection?.Features
                .Get<ISecureConnectionFeature>().CloseEncryptedControlStream;
            // AUTH TLS 完成后，库（TlsEnableServerCommandHandler）把这个委托从「无捕获的静态
            // lambda（Target 是 Roslyn 的缓存单例，类型名恰为 "<>c"）」换成「捕获了 feature 的
            // 闭包（类型名 "<>c__DisplayClass…"）」。为什么不用别的信号：
            //   - IFtpConnection.IsSecure 在 3.1.2 里永远抛 NotSupportedException（连 FTPS 连接也一样）；
            //   - CloseEncryptedControlStream 默认值非 null（ct => Task.CompletedTask），不能用 null 判断。
            // Roslyn 对非捕获 lambda / 捕获闭包的命名规则是稳定的；FubarDev 锁定 3.1.2，升级时复核。
            var isTls = closeHook is not null
                        && !string.Equals(closeHook.Target?.GetType().Name, "<>c", StringComparison.Ordinal);
            if (!isTls)
            {
                return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
            }
        }

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
/// 为每个 FTP 会话创建基于 VFS 的 <see cref="IUnixFileSystem"/> 的工厂。
/// </summary>
public sealed class MountFtpFileSystemFactory : IFileSystemClassFactory
{
    private readonly MountService _mount;

    public MountFtpFileSystemFactory(MountService mount)
    {
        _mount = mount;
    }

    /// <summary>为给定账户创建一个 <see cref="MountUnixFileSystem"/> 实例。</summary>
    public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var fs = new MountUnixFileSystem(_mount);
        return Task.FromResult<IUnixFileSystem>(fs);
    }
}

/// <summary>
/// 适配器：将 <see cref="MountFileSystem"/> 暴露为 FTP 服务器所需的
/// <see cref="IUnixFileSystem"/>。
/// </summary>
public sealed class MountUnixFileSystem : IUnixFileSystem
{
    private readonly MountService _mount;
    private readonly MountUnixDirectoryEntry _root;

    public MountUnixFileSystem(MountService mount)
    {
        _mount = mount;
        _root = new MountUnixDirectoryEntry(_mount, UPath.Root, "/");
    }

    public StringComparer FileSystemEntryComparer => StringComparer.OrdinalIgnoreCase;
    public IUnixDirectoryEntry Root => _root;
    public bool SupportsAppend => true;
    public bool SupportsNonEmptyDirectoryDelete => true;

    public Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(
        IUnixDirectoryEntry directoryEntry,
        CancellationToken cancellationToken)
    {
        var mountEntry = (MountUnixDirectoryEntry)directoryEntry;
        var entries = new List<IUnixFileSystemEntry>();

        // 遍历目录下的文件与子目录，统一包装为对应的 Unix 条目。
        if (_mount.Root.DirectoryExists(mountEntry.Path))
        {
            foreach (var child in _mount.Root.EnumeratePaths(mountEntry.Path, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both))
            {
                entries.Add(_mount.Root.FileExists(child)
                    ? new MountUnixFileEntry(_mount, child, child.GetName())
                    : new MountUnixDirectoryEntry(_mount, child, child.GetName() + "/"));
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
        var mountEntry = (MountUnixDirectoryEntry)directoryEntry;
        var childPath = mountEntry.Path / name;

        if (_mount.Root.FileExists(childPath))
            return Task.FromResult<IUnixFileSystemEntry?>(new MountUnixFileEntry(_mount, childPath, name));
        if (_mount.Root.DirectoryExists(childPath))
            return Task.FromResult<IUnixFileSystemEntry?>(new MountUnixDirectoryEntry(_mount, childPath, name + "/"));

        return Task.FromResult<IUnixFileSystemEntry?>(null);
    }

    /// <summary>以只读方式打开文件（支持从指定偏移开始读取），供 FTP 下载使用。</summary>
    public Task<Stream> OpenReadAsync(
        IUnixFileEntry fileEntry,
        long startPosition,
        CancellationToken cancellationToken)
    {
        var mountEntry = (MountUnixFileEntry)fileEntry;
        var stream = _mount.Root.OpenFile(mountEntry.Path, FileMode.Open, FileAccess.Read);
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
        var mountDir = (MountUnixDirectoryEntry)targetDirectory;
        var filePath = mountDir.Path / fileName;

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
        var mountEntry = (MountUnixFileEntry)fileEntry;
        var fs = _mount.Root.OpenFile(mountEntry.Path, FileMode.Create, FileAccess.Write);
        return Task.FromResult<IBackgroundTransfer?>(new StreamBackgroundTransfer(data, fs));
    }

    /// <summary>以追加方式写入文件（支持续传），返回后台传输会话。</summary>
    public Task<IBackgroundTransfer?> AppendAsync(
        IUnixFileEntry fileEntry,
        long? startPosition,
        Stream data,
        CancellationToken cancellationToken)
    {
        var mountEntry = (MountUnixFileEntry)fileEntry;
        var fs = _mount.Root.OpenFile(mountEntry.Path, FileMode.Append, FileAccess.Write);
        return Task.FromResult<IBackgroundTransfer?>(new StreamBackgroundTransfer(data, fs));
    }

    /// <summary>在目标目录下创建子目录并返回对应条目。</summary>
    public Task<IUnixDirectoryEntry> CreateDirectoryAsync(
        IUnixDirectoryEntry targetDirectory,
        string directoryName,
        CancellationToken cancellationToken)
    {
        var mountDir = (MountUnixDirectoryEntry)targetDirectory;
        var newPath = mountDir.Path / directoryName;
        _mount.Root.CreateDirectory(newPath);
        return Task.FromResult<IUnixDirectoryEntry>(new MountUnixDirectoryEntry(_mount, newPath, directoryName + "/"));
    }

    /// <summary>删除文件或（递归）删除目录。</summary>
    public Task UnlinkAsync(
        IUnixFileSystemEntry entry,
        CancellationToken cancellationToken)
    {
        var mountEntry = (IMountUnixEntry)entry;
        if (_mount.Root.FileExists(mountEntry.Path))
            _mount.Root.DeleteFile(mountEntry.Path);
        else if (_mount.Root.DirectoryExists(mountEntry.Path))
            _mount.Root.DeleteDirectory(mountEntry.Path, true);
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
        var mountSource = (IMountUnixEntry)source;
        var mountTarget = (MountUnixDirectoryEntry)target;
        var newPath = mountTarget.Path / fileName;

        if (_mount.Root.FileExists(mountSource.Path))
            _mount.Root.MoveFile(mountSource.Path, newPath);
        else if (_mount.Root.DirectoryExists(mountSource.Path))
            _mount.Root.MoveDirectory(mountSource.Path, newPath);

        return Task.FromResult<IUnixFileSystemEntry>(
            _mount.Root.FileExists(newPath)
                ? new MountUnixFileEntry(_mount, newPath, fileName)
                : new MountUnixDirectoryEntry(_mount, newPath, fileName + "/"));
    }

    /// <summary>设置条目时间戳（各驱动对逐条目时间戳的更新支持并不统一，故为空操作）。</summary>
    public Task<IUnixFileSystemEntry> SetMacTimeAsync(
        IUnixFileSystemEntry entry,
        DateTimeOffset? modify,
        DateTimeOffset? access,
        DateTimeOffset? create,
        CancellationToken cancellationToken)
    {
        // 当前抽象未提供跨驱动一致的逐条目时间戳更新接口。
        return Task.FromResult(entry);
    }
}

/// <summary>统一暴露 VFS 路径，便于 FTP 条目与挂载文件系统之间互转。</summary>
public interface IMountUnixEntry
{
    UPath Path { get; }
}

/// <summary>FTP 会话中的文件条目，映射到 挂载文件系统的文件。</summary>
public sealed class MountUnixFileEntry : IUnixFileEntry, IMountUnixEntry
{
    private readonly MountService _mount;
    public UPath Path { get; }

    /// <summary>从 VFS 文件条目读取大小与时间信息，构造对应的 FTP 文件条目。</summary>
    public MountUnixFileEntry(MountService mount, UPath path, string name)
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

/// <summary>FTP 会话中的目录条目，映射到 挂载文件系统的目录。</summary>
public sealed class MountUnixDirectoryEntry : IUnixDirectoryEntry, IMountUnixEntry
{
    private readonly MountService _mount;
    public UPath Path { get; }

    /// <summary>构造目录条目；根路径（UPath.Root）被标记为 IsRoot。</summary>
    public MountUnixDirectoryEntry(MountService mount, UPath path, string name)
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
