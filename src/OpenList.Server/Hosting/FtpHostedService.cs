using System.Net;
using System.Security.Claims;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.Features;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.Generic;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
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
/// <para>
/// 关键点：**每个会话的文件系统以该用户的 BasePath 为根**。此前不论谁登录都从全局
/// <c>UPath.Root</c> 开始，受限用户（甚至只有读权限的账号）都能在 FTP 里看到并改写整站文件，
/// 登录身份只用于「是否放行」，从未转化为文件访问上下文。
/// </para>
/// </summary>
public sealed class MountFtpFileSystemFactory : IFileSystemClassFactory
{
    private readonly MountService _mount;
    private readonly IServiceScopeFactory _scopes;

    public MountFtpFileSystemFactory(MountService mount, IServiceScopeFactory scopes)
    {
        _mount = mount;
        _scopes = scopes;
    }

    /// <summary>为给定账户创建一个以该用户 BasePath 为根的 <see cref="MountUnixFileSystem"/>。</summary>
    public async Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var user = await ResolveUserAsync(accountInformation);
        return new MountUnixFileSystem(_mount, user, _scopes);
    }

    /// <summary>
    /// 从 FTP 登录身份解析完整的 OpenList 用户（含 BasePath 与权限位）。
    /// 解析失败时返回 <see cref="User.Disabled"/> 的占位用户 —— 宁可见不到任何文件，
    /// 也不能退回全局根目录。
    /// </summary>
    private async Task<User> ResolveUserAsync(IAccountInformation accountInformation)
    {
        var fallback = new User
        {
            Id = 0,
            Username = accountInformation.FtpUser?.Identity?.Name ?? "ftp",
            Role = UserRole.Guest,
            Permission = 0,
            Disabled = true,
        };

        long? userId = null;
        string? userName = null;
        try
        {
            var principal = accountInformation.FtpUser;
            var idClaim = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (long.TryParse(idClaim, out var parsed)) userId = parsed;
            userName = principal?.Identity?.Name;
        }
        catch
        {
            // 兼容层行为变化时退回按用户名查找。
            userName = accountInformation.FtpUser?.Identity?.Name;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var users = scope.ServiceProvider.GetService<IUserService>();
            if (users is null) return fallback;

            if (userId is not null)
            {
                var byId = await users.GetAsync(userId.Value);
                if (byId.Code == RespCodes.Ok && byId.Data is { } u) return u;
            }
            if (!string.IsNullOrEmpty(userName))
            {
                var byName = await users.GetByNameAsync(userName);
                if (byName.Code == RespCodes.Ok && byName.Data is { } u) return u;
            }
        }
        catch
        {
            // 数据库暂时不可用时同样按「拒绝」处理。
        }
        return fallback;
    }
}

/// <summary>
/// 适配器：将 <see cref="MountFileSystem"/> 暴露为 FTP 服务器所需的
/// <see cref="IUnixFileSystem"/>，并把根目录固定为登录用户的 BasePath。
/// <para>
/// 读写都要过 <see cref="FileAccessService"/>（与 <c>/api/fs</c>、WebDAV、S3 同一套判定）：
/// 读 = 隐藏条目 / ReadUsers / 目录密码；写 = 权限位 + meta WriteUsers。
/// 单例服务不能持有 scoped 依赖，因此每次判定用短生命周期作用域解析。
/// </para>
/// </summary>
public sealed class MountUnixFileSystem : IUnixFileSystem
{
    private readonly MountService _mount;
    private readonly User _user;
    private readonly IServiceScopeFactory? _scopes;
    private readonly UPath _rootPath;
    private readonly MountUnixDirectoryEntry _root;

    /// <param name="mount">挂载服务。</param>
    /// <param name="user">会话用户；为空按无权限访客处理。</param>
    /// <param name="scopes">作用域工厂，用于解析 scoped 的授权服务。</param>
    public MountUnixFileSystem(MountService mount, User? user = null, IServiceScopeFactory? scopes = null)
    {
        _mount = mount;
        _user = user ?? new User { Id = 0, Username = "", Role = UserRole.Guest, Permission = 0 };
        _scopes = scopes;
        _rootPath = new UPath(MetaAccess.Normalize(string.IsNullOrEmpty(_user.BasePath) ? "/" : _user.BasePath));
        _root = new MountUnixDirectoryEntry(_mount, _user, _rootPath, "/", _rootPath);
    }

    public StringComparer FileSystemEntryComparer => StringComparer.OrdinalIgnoreCase;
    public IUnixDirectoryEntry Root => _root;
    public bool SupportsAppend => true;
    public bool SupportsNonEmptyDirectoryDelete => true;

    public async Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(
        IUnixDirectoryEntry directoryEntry,
        CancellationToken cancellationToken)
    {
        var mountEntry = (MountUnixDirectoryEntry)directoryEntry;
        var entries = new List<IUnixFileSystemEntry>();

        // 一次 LIST 只用一个作用域 + 一次 meta 查询：逐项查库会让大目录列举退化成 N 次查询。
        using var scope = _scopes?.CreateScope();
        var access = scope?.ServiceProvider.GetService<FileAccessService>();
        if (access is null) return entries;
        var metas = await access.LoadMetasAsync(cancellationToken);

        if (_mount.Root.DirectoryExists(mountEntry.Path))
        {
            foreach (var child in _mount.Root.EnumeratePaths(mountEntry.Path, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both))
            {
                // 逐项可见性：隐藏规则与 ReadUsers 同样约束 FTP 列表。
                if (!access.CanSee(_user, child.FullName, null, metas)) continue;
                entries.Add(_mount.Root.FileExists(child)
                    ? new MountUnixFileEntry(_mount, _user, child, child.GetName())
                    : new MountUnixDirectoryEntry(_mount, _user, child, child.GetName() + "/", _rootPath));
            }
        }

        return entries;
    }

    /// <summary>按名称在目录下查找子项（文件/目录）；不可见时与不存在一样返回 null。</summary>
    public async Task<IUnixFileSystemEntry?> GetEntryByNameAsync(
        IUnixDirectoryEntry directoryEntry,
        string name,
        CancellationToken cancellationToken)
    {
        var mountEntry = (MountUnixDirectoryEntry)directoryEntry;
        var childPath = mountEntry.Path / name;

        if (!await CanSeeAsync(childPath, cancellationToken)) return null;

        if (_mount.Root.FileExists(childPath))
            return new MountUnixFileEntry(_mount, _user, childPath, name);
        if (_mount.Root.DirectoryExists(childPath))
            return new MountUnixDirectoryEntry(_mount, _user, childPath, name + "/", _rootPath);

        return null;
    }

    /// <summary>以只读方式打开文件（支持从指定偏移开始读取），读取前先过读门禁。</summary>
    public async Task<Stream> OpenReadAsync(
        IUnixFileEntry fileEntry,
        long startPosition,
        CancellationToken cancellationToken)
    {
        var mountEntry = (MountUnixFileEntry)fileEntry;
        await EnsureAsync(mountEntry.Path, FileOperation.Read, cancellationToken);
        var stream = _mount.Root.OpenFile(mountEntry.Path, FileMode.Open, FileAccess.Read);
        if (startPosition > 0) stream.Position = startPosition;
        return stream;
    }

    /// <summary>在目标目录创建新文件（必要时先建父目录），返回后台传输会话用于写入。</summary>
    public async Task<IBackgroundTransfer?> CreateAsync(
        IUnixDirectoryEntry targetDirectory,
        string fileName,
        Stream data,
        CancellationToken cancellationToken)
    {
        var mountDir = (MountUnixDirectoryEntry)targetDirectory;
        var filePath = mountDir.Path / fileName;
        await EnsureAsync(filePath, FileOperation.ContentWrite, cancellationToken);

        var dirPath = filePath.GetDirectory();
        if (!_mount.Root.DirectoryExists(dirPath))
            _mount.Root.CreateDirectory(dirPath);

        var fs = _mount.Root.OpenFile(filePath, FileMode.Create, FileAccess.Write);
        return new StreamBackgroundTransfer(data, fs);
    }

    /// <summary>以覆盖方式替换文件内容，返回后台传输会话。</summary>
    public async Task<IBackgroundTransfer?> ReplaceAsync(
        IUnixFileEntry fileEntry,
        Stream data,
        CancellationToken cancellationToken)
    {
        var mountEntry = (MountUnixFileEntry)fileEntry;
        await EnsureAsync(mountEntry.Path, FileOperation.ContentWrite, cancellationToken);
        var fs = _mount.Root.OpenFile(mountEntry.Path, FileMode.Create, FileAccess.Write);
        return new StreamBackgroundTransfer(data, fs);
    }

    /// <summary>以追加方式写入文件（支持续传），返回后台传输会话。</summary>
    public async Task<IBackgroundTransfer?> AppendAsync(
        IUnixFileEntry fileEntry,
        long? startPosition,
        Stream data,
        CancellationToken cancellationToken)
    {
        var mountEntry = (MountUnixFileEntry)fileEntry;
        await EnsureAsync(mountEntry.Path, FileOperation.ContentWrite, cancellationToken);
        var fs = _mount.Root.OpenFile(mountEntry.Path, FileMode.Append, FileAccess.Write);
        return new StreamBackgroundTransfer(data, fs);
    }

    /// <summary>在目标目录下创建子目录并返回对应条目。</summary>
    public async Task<IUnixDirectoryEntry> CreateDirectoryAsync(
        IUnixDirectoryEntry targetDirectory,
        string directoryName,
        CancellationToken cancellationToken)
    {
        var mountDir = (MountUnixDirectoryEntry)targetDirectory;
        var newPath = mountDir.Path / directoryName;
        await EnsureAsync(newPath, FileOperation.ContentWrite, cancellationToken);
        _mount.Root.CreateDirectory(newPath);
        return new MountUnixDirectoryEntry(_mount, _user, newPath, directoryName + "/", _rootPath);
    }

    /// <summary>删除文件或（递归）删除目录。</summary>
    public async Task UnlinkAsync(
        IUnixFileSystemEntry entry,
        CancellationToken cancellationToken)
    {
        var mountEntry = (IMountUnixEntry)entry;
        // 权限位 7（可删除）+ 父目录 meta 写入白名单。
        if (!_user.CanRemove() && !_user.IsAdmin())
            throw new UnauthorizedAccessException("permission denied");
        await EnsureAsync(mountEntry.Path, FileOperation.MetaWrite, cancellationToken);

        if (_mount.Root.FileExists(mountEntry.Path))
            _mount.Root.DeleteFile(mountEntry.Path);
        else if (_mount.Root.DirectoryExists(mountEntry.Path))
            _mount.Root.DeleteDirectory(mountEntry.Path, true);
    }

    /// <summary>将文件/目录移动（重命名）到目标目录下。</summary>
    public async Task<IUnixFileSystemEntry> MoveAsync(
        IUnixDirectoryEntry parent,
        IUnixFileSystemEntry source,
        IUnixDirectoryEntry target,
        string fileName,
        CancellationToken cancellationToken)
    {
        var mountSource = (IMountUnixEntry)source;
        var mountTarget = (MountUnixDirectoryEntry)target;
        var newPath = mountTarget.Path / fileName;

        // MOVE 同时承担「移动」与「重命名」：任一权限位满足即可，但两端都要过 meta 写门禁。
        if (!_user.CanMove() && !_user.CanRename() && !_user.IsAdmin())
            throw new UnauthorizedAccessException("permission denied");
        await EnsureAsync(mountSource.Path, FileOperation.MetaWrite, cancellationToken);
        await EnsureAsync(newPath, FileOperation.MetaWrite, cancellationToken);

        if (_mount.Root.FileExists(mountSource.Path))
            _mount.Root.MoveFile(mountSource.Path, newPath);
        else if (_mount.Root.DirectoryExists(mountSource.Path))
            _mount.Root.MoveDirectory(mountSource.Path, newPath);

        return _mount.Root.FileExists(newPath)
            ? new MountUnixFileEntry(_mount, _user, newPath, fileName)
            : new MountUnixDirectoryEntry(_mount, _user, newPath, fileName + "/", _rootPath);
    }

    /// <summary>设置条目时间戳（各驱动对逐条目时间戳的更新支持并不统一，故为空操作）。</summary>
    public Task<IUnixFileSystemEntry> SetMacTimeAsync(
        IUnixFileSystemEntry entry,
        DateTimeOffset? modify,
        DateTimeOffset? access,
        DateTimeOffset? create,
        CancellationToken cancellationToken) => Task.FromResult(entry);

    /// <summary>读可见性（与 PROPFIND / S3 列表同一判定）。</summary>
    private async Task<bool> CanSeeAsync(UPath path, CancellationToken ct)
    {
        if (_scopes is null) return false;
        using var scope = _scopes.CreateScope();
        var access = scope.ServiceProvider.GetService<FileAccessService>();
        if (access is null) return false;
        return await access.CanSeeAsync(_user, path.FullName, null, ct);
    }

    /// <summary>授权校验；拒绝时抛 <see cref="UnauthorizedAccessException"/>（FubarDev 映射为 550）。</summary>
    private async Task EnsureAsync(UPath path, FileOperation op, CancellationToken ct)
    {
        if (_scopes is null) throw new UnauthorizedAccessException("permission denied");
        using var scope = _scopes.CreateScope();
        var access = scope.ServiceProvider.GetService<FileAccessService>();
        if (access is null) throw new UnauthorizedAccessException("permission denied");
        var decision = await access.AuthorizeAsync(_user, path.FullName, op, null, ct);
        if (!decision.Allowed) throw new UnauthorizedAccessException(decision.Message);
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
    public MountUnixFileEntry(MountService mount, User user, UPath path, string name)
    {
        _mount = mount;
        Path = path;
        Name = name;
        var entry = mount.Root.GetFileEntry(path);
        Size = entry.Length;
        LastWriteTime = new DateTimeOffset(entry.LastWriteTime.ToUniversalTime(), TimeSpan.Zero);
        CreatedTime = new DateTimeOffset(entry.CreationTime.ToUniversalTime(), TimeSpan.Zero);
        Permissions = UnixPermissionsFor(user);
    }

    public string Name { get; }
    public long Size { get; }
    public DateTimeOffset? LastWriteTime { get; }
    public DateTimeOffset? CreatedTime { get; }
    public IUnixPermissions Permissions { get; }
    public string Owner => "root";
    public string Group => "root";
    public long NumberOfLinks => 1;

    /// <summary>
    /// 权限展示必须反映真实能力：此前对所有客户端固定宣告 rwx，
    /// 只读账号在资源管理器里看起来可以删除/改名，实际每次操作都失败。
    /// </summary>
    internal static IUnixPermissions UnixPermissionsFor(User user)
    {
        var canWrite = user.CanWriteContent() || user.IsAdmin();
        var canModify = canWrite || user.CanRename() || user.CanMove() || user.CanRemove();
        return new GenericUnixPermissions(
            new GenericAccessMode(true, canModify, true),
            new GenericAccessMode(true, false, true),
            new GenericAccessMode(true, false, true));
    }
}

/// <summary>FTP 会话中的目录条目，映射到 挂载文件系统的目录。</summary>
public sealed class MountUnixDirectoryEntry : IUnixDirectoryEntry, IMountUnixEntry
{
    private readonly MountService _mount;
    public UPath Path { get; }

    /// <summary>构造目录条目；等于会话根（用户 BasePath）时标记为 IsRoot。</summary>
    public MountUnixDirectoryEntry(MountService mount, User user, UPath path, string name, UPath rootPath)
    {
        _mount = mount;
        Path = path;
        Name = name;
        IsRoot = path == rootPath;
        Permissions = MountUnixFileEntry.UnixPermissionsFor(user);
    }

    public string Name { get; }
    public DateTimeOffset? LastWriteTime { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CreatedTime { get; } = DateTimeOffset.UtcNow;
    public IUnixPermissions Permissions { get; }
    public string Owner => "root";
    public string Group => "root";
    public long NumberOfLinks => 1;
    public bool IsDeletable => !IsRoot;
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
