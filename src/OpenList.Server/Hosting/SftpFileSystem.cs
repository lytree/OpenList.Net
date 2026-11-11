using System.Security.Cryptography;
using JustSFTP.Protocol;
using JustSFTP.Protocol.Enums;
using JustSFTP.Protocol.Models;
using JustSFTP.Protocol.Models.Responses;
using JustSFTP.Server;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Infrastructure.Services;
using OpenList.Vfs;

namespace OpenList.Server.Hosting;

/// <summary>
/// SFTP 会话文件系统：把 JustSFTP 的 v3 协议命令映射到 <see cref="MountFileSystem"/>。
/// <para>
/// 每个会话以登录用户的 BasePath 为根，读写判定复用 <see cref="FileAccessService"/>
/// （与 <c>/api/fs</c>、WebDAV、S3、FTP 同一套规则）：读 = 隐藏条目 / ReadUsers / 目录密码；
/// 写 = 权限位 + meta WriteUsers。
/// </para>
/// </summary>
internal sealed class SftpSessionFileSystem : ISFTPHandler
{
    private readonly MountService _mount;
    private readonly User _user;
    private readonly IServiceScopeFactory _scopes;
    private readonly UPath _root;
    private readonly Dictionary<string, SftpOpenHandle> _handles = new(StringComparer.Ordinal);
    private readonly object _handlesGate = new();
    private bool _disposed;

    internal SftpSessionFileSystem(MountService mount, User user, IServiceScopeFactory scopes)
    {
        _mount = mount;
        _user = user;
        _scopes = scopes;
        _root = new UPath(MetaAccess.Normalize(string.IsNullOrEmpty(user.BasePath) ? "/" : user.BasePath));
    }

    /// <summary>初始化并回显客户端扩展。</summary>
    public Task<SFTPExtensions> Init(uint version, SFTPExtensions extensions, CancellationToken ct)
        => Task.FromResult(extensions ?? new SFTPExtensions());

    /// <summary>把客户端路径规范化（相对会话根，禁止越出根目录）。</summary>
    private static string Canonicalize(string? raw)
    {
        var segments = new List<string>();
        foreach (var segment in (raw ?? "/").Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new UnauthorizedAccessException("path escapes the session root");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>把客户端路径映射到 VFS 路径。</summary>
    private UPath Resolve(SFTPPath path)
    {
        var canonical = Canonicalize(path.Path);
        return canonical == "/" ? _root : _root / canonical.TrimStart('/');
    }

    /// <summary>读授权（隐藏/密码/ReadUsers）。</summary>
    private async Task AuthorizeReadAsync(UPath path, CancellationToken ct)
        => await AuthorizeAsync(path, FileOperation.Read, ct);

    /// <summary>写内容授权（权限位 3 + meta 写白名单）。</summary>
    private async Task AuthorizeWriteAsync(UPath path, CancellationToken ct)
        => await AuthorizeAsync(path, FileOperation.ContentWrite, ct);

    /// <summary>元数据写授权（改名/删除等）。</summary>
    private async Task AuthorizeMetaAsync(UPath path, CancellationToken ct)
        => await AuthorizeAsync(path, FileOperation.MetaWrite, ct);

    private async Task AuthorizeAsync(UPath path, FileOperation operation, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<FileAccessService>();
        var decision = await access.AuthorizeAsync(_user, path.FullName, operation, null, ct);
        if (!decision.Allowed)
            throw new HandlerException(Status.PermissionDenied, decision.Message);
    }

    private static HandlerException Map(Exception ex) => ex switch
    {
        HandlerException handler => handler,
        UnauthorizedAccessException => new HandlerException(Status.PermissionDenied, ex.Message),
        FileNotFoundException or DirectoryNotFoundException => new HandlerException(Status.NoSuchFile, ex.Message),
        NotSupportedException => new HandlerException(Status.OperationUnsupported, ex.Message),
        _ => new HandlerException(Status.Failure, ex.Message),
    };

    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Map(ex);
        }
    }

    private static async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Map(ex);
        }
    }

    // --- 文件打开 / 读写 ---

    /// <summary>打开文件；写模式先过内容写门禁。</summary>
    public Task<byte[]> Open(SFTPPath path, FileMode mode, FileAccess access, SFTPAttributes attributes, CancellationToken ct)
        => Run(async () =>
        {
            var vfsPath = Resolve(path);
            var writable = access.HasFlag(FileAccess.Write) || mode is FileMode.Create or FileMode.CreateNew
                or FileMode.OpenOrCreate or FileMode.Truncate or FileMode.Append;

            if (writable)
                await AuthorizeWriteAsync(vfsPath, ct);
            else
                await AuthorizeReadAsync(vfsPath, ct);

            var stream = _mount.Root.OpenFile(vfsPath, mode, access);
            return AddHandle(new SftpOpenHandle(stream, vfsPath, false, access));
        });

    /// <summary>关闭句柄（文件流或目录句柄）。</summary>
    public Task Close(byte[] handle, CancellationToken ct)
        => Run(() =>
        {
            var entry = TakeHandle(handle);
            entry.Stream?.Dispose();
            return Task.CompletedTask;
        });

    /// <summary>按偏移读取；返回空数组表示 EOF。</summary>
    public Task<byte[]> Read(byte[] handle, ulong offset, uint length, CancellationToken ct)
        => Run(async () =>
        {
            var entry = GetHandle(handle);
            if (entry.Stream is null)
                throw new HandlerException(Status.Failure, "handle is not a file");

            var buffer = new byte[Math.Max(1, (int)Math.Min(length, 1 * 1024 * 1024))];
            SeekTo(entry, offset);
            var read = await entry.Stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            entry.Position += read;
            return read == buffer.Length ? buffer : buffer[..read];
        });

    /// <summary>按偏移写入。</summary>
    public Task Write(byte[] handle, ulong offset, byte[] data, CancellationToken ct)
        => Run(async () =>
        {
            var entry = GetHandle(handle);
            if (entry.Stream is null)
                throw new HandlerException(Status.Failure, "handle is not a file");

            SeekTo(entry, offset);
            await entry.Stream.WriteAsync(data, ct);
            await entry.Stream.FlushAsync(ct);
            entry.Position += data.Length;
        });

    /// <summary>
    /// 把文件流定位到指定偏移。可寻址流直接 set Position；
    /// 不可寻址的驱动流（部分云端/网络驱动）只允许顺序访问，乱序读需由客户端换一种写法。
    /// </summary>
    private static void SeekTo(SftpOpenHandle handle, ulong offset)
    {
        var stream = handle.Stream!;
        if (stream.CanSeek)
        {
            stream.Position = (long)offset;
        }
        else if (handle.Position != (long)offset)
        {
            throw new HandlerException(Status.OperationUnsupported, "out-of-order access on a non-seekable stream");
        }
    }

    // --- 属性 ---

    /// <summary>lstat（本实现无符号链接，等同 stat）。</summary>
    public Task<SFTPAttributes> LStat(SFTPPath path, CancellationToken ct)
        => Stat(path, ct);

    /// <summary>按句柄返回属性。</summary>
    public Task<SFTPAttributes> FStat(byte[] handle, CancellationToken ct)
        => Run(() =>
        {
            var entry = GetHandle(handle);
            return Task.FromResult(BuildAttributes(entry.Path, entry.IsDirectory));
        });

    /// <summary>读取路径属性。</summary>
    public Task<SFTPAttributes> Stat(SFTPPath path, CancellationToken ct)
        => Run(async () =>
        {
            var vfsPath = Resolve(path);
            await AuthorizeReadAsync(vfsPath, ct);

            if (_mount.Root.DirectoryExists(vfsPath))
                return BuildAttributes(vfsPath, true);
            if (_mount.Root.FileExists(vfsPath))
                return BuildAttributes(vfsPath, false);
            throw new HandlerException(Status.NoSuchFile, "no such file");
        });

    /// <summary>设置属性：仅支持修改最后写入时间，其余字段忽略。</summary>
    public Task SetStat(SFTPPath path, SFTPAttributes attributes, CancellationToken ct)
        => Run(async () =>
        {
            var vfsPath = Resolve(path);
            await AuthorizeWriteAsync(vfsPath, ct);
            ApplyAttributes(vfsPath, attributes);
        });

    /// <summary>按句柄设置属性。</summary>
    public Task FSetStat(byte[] handle, SFTPAttributes attributes, CancellationToken ct)
        => Run(async () =>
        {
            var entry = GetHandle(handle);
            await AuthorizeWriteAsync(entry.Path, ct);
            ApplyAttributes(entry.Path, attributes);
        });

    private void ApplyAttributes(UPath path, SFTPAttributes attributes)
    {
        if (attributes.LastModifiedTime is { } modified)
        {
            try
            {
                _mount.Root.SetLastWriteTime(path, modified.UtcDateTime);
            }
            catch (NotSupportedException)
            {
                // 非本地驱动可能不支持改时间；SFTP 语义允许忽略。
            }
        }
    }

    private SFTPAttributes BuildAttributes(UPath path, bool isDirectory)
    {
        var canWrite = _user.CanWriteContent() || _user.IsAdmin();
        var permissions = Permissions.UserRead | Permissions.GroupRead | Permissions.OtherRead;
        permissions |= isDirectory ? Permissions.Directory | Permissions.UserExecute : Permissions.RegularFile;
        if (canWrite)
            permissions |= Permissions.UserWrite;

        if (isDirectory)
        {
            var dir = _mount.Root.GetDirectoryEntry(path);
            return new SFTPAttributes
            {
                Permissions = permissions,
                LastModifiedTime = ToOffset(dir.LastWriteTime),
                LastAccessedTime = ToOffset(dir.CreationTime),
            };
        }

        var file = _mount.Root.GetFileEntry(path);
        return new SFTPAttributes
        {
            FileSize = (ulong)Math.Max(0, file.Length),
            Permissions = permissions,
            LastModifiedTime = ToOffset(file.LastWriteTime),
            LastAccessedTime = ToOffset(file.CreationTime),
        };
    }

    private static DateTimeOffset ToOffset(DateTime time)
        => new(time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime(), TimeSpan.Zero);

    // --- 目录 ---

    /// <summary>打开目录句柄。</summary>
    public Task<byte[]> OpenDir(SFTPPath path, CancellationToken ct)
        => Run(async () =>
        {
            var vfsPath = Resolve(path);
            await AuthorizeReadAsync(vfsPath, ct);
            if (!_mount.Root.DirectoryExists(vfsPath))
                throw new HandlerException(Status.NoSuchFile, "no such directory");
            return AddHandle(new SftpOpenHandle(null, vfsPath, true, FileAccess.Read));
        });

    /// <summary>枚举目录项；不可见条目被跳过。</summary>
    /// <remarks>
    /// 目录内容在**首次** readdir 时一次性取出并缓存到句柄上，后续调用从上次位置继续。
    /// JustSFTP 每次 readdir 只消费 128 项、然后丢弃枚举器，且以「空枚举」作为 EOF 信号；
    /// 若每次都返回从头开始的枚举器，客户端会永远读不到 EOF 并无限重复同一批条目。
    /// </remarks>
    public Task<IEnumerator<SFTPName>> ReadDir(byte[] handle, CancellationToken ct)
        => Run(async () =>
        {
            var entry = GetHandle(handle);
            if (!entry.IsDirectory)
                throw new HandlerException(Status.Failure, "handle is not a directory");

            if (entry.Names is null)
            {
                var names = new List<SFTPName>();
                using var scope = _scopes.CreateScope();
                var access = scope.ServiceProvider.GetRequiredService<FileAccessService>();
                var metas = await access.LoadMetasAsync(ct);

                foreach (var child in _mount.Root.EnumeratePaths(entry.Path, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both))
                {
                    if (!access.CanSee(_user, child.FullName, null, metas))
                        continue;
                    var isDirectory = _mount.Root.DirectoryExists(child);
                    names.Add(new SFTPName(child.GetName(), child.GetName() + (isDirectory ? "/" : ""),
                        BuildAttributes(child, isDirectory)));
                }

                entry.Names = names;
            }

            return (IEnumerator<SFTPName>)new DirectoryPageEnumerator(entry);
        });

    // --- 目录/文件管理 ---

    /// <summary>删除文件（权限位 7 + meta 写白名单）。</summary>
    public Task Remove(SFTPPath path, CancellationToken ct)
        => Run(async () =>
        {
            var vfsPath = Resolve(path);
            if (!_user.CanRemove() && !_user.IsAdmin())
                throw new HandlerException(Status.PermissionDenied, "permission denied");
            await AuthorizeMetaAsync(vfsPath, ct);

            if (_mount.Root.FileExists(vfsPath))
                _mount.Root.DeleteFile(vfsPath);
            else
                throw new HandlerException(Status.Failure, "not a file");
        });

    /// <summary>创建目录（权限位 3 + meta 写白名单）。</summary>
    public Task MakeDir(SFTPPath path, SFTPAttributes attributes, CancellationToken ct)
        => Run(async () =>
        {
            var vfsPath = Resolve(path);
            if (!_user.CanWriteContent() && !_user.IsAdmin())
                throw new HandlerException(Status.PermissionDenied, "permission denied");
            await AuthorizeWriteAsync(vfsPath, ct);
            _mount.Root.CreateDirectory(vfsPath);
        });

    /// <summary>删除空目录（权限位 7 + meta 写白名单）。</summary>
    public Task RemoveDir(SFTPPath path, CancellationToken ct)
        => Run(async () =>
        {
            var vfsPath = Resolve(path);
            if (!_user.CanRemove() && !_user.IsAdmin())
                throw new HandlerException(Status.PermissionDenied, "permission denied");
            await AuthorizeMetaAsync(vfsPath, ct);
            _mount.Root.DeleteDirectory(vfsPath, false);
        });

    /// <summary>改名/移动（权限位 4/5 + 两端 meta 写白名单）。</summary>
    public Task Rename(SFTPPath oldPath, SFTPPath newPath, CancellationToken ct)
        => Run(async () =>
        {
            var source = Resolve(oldPath);
            var destination = Resolve(newPath);
            if (!_user.CanMove() && !_user.CanRename() && !_user.IsAdmin())
                throw new HandlerException(Status.PermissionDenied, "permission denied");
            await AuthorizeMetaAsync(source, ct);
            await AuthorizeMetaAsync(destination, ct);

            if (_mount.Root.FileExists(source))
                _mount.Root.MoveFile(source, destination);
            else if (_mount.Root.DirectoryExists(source))
                _mount.Root.MoveDirectory(source, destination);
            else
                throw new HandlerException(Status.NoSuchFile, "no such file");
        });

    /// <summary>规范化路径（相对会话根）。</summary>
    public Task<SFTPPath> RealPath(SFTPPath path, CancellationToken ct)
        => Run(() => Task.FromResult(new SFTPPath(Canonicalize(path.Path))));

    /// <summary>本实现不支持符号链接。</summary>
    public Task<SFTPName> ReadLink(SFTPPath linkPath, CancellationToken ct)
        => throw new HandlerException(Status.OperationUnsupported, "readlink is not supported");

    /// <summary>本实现不支持符号链接。</summary>
    public Task SymLink(SFTPPath linkPath, SFTPPath targetPath, CancellationToken ct)
        => throw new HandlerException(Status.OperationUnsupported, "symlink is not supported");

    /// <summary>未知扩展请求一律不支持。</summary>
    public Task<SFTPResponse> Extended(uint requestId, string requestType, Stream payload, CancellationToken ct)
        => throw new HandlerException(Status.OperationUnsupported, $"unsupported extension: {requestType}");

    /// <summary>状态码对应的标准文案。</summary>
    public string GetDefaultStatusString(Status status) => status switch
    {
        Status.Ok => "Success",
        Status.EndOfFile => "End of file",
        Status.NoSuchFile => "No such file",
        Status.PermissionDenied => "Permission denied",
        Status.Failure => "Failure",
        Status.BadMessage => "Bad message",
        Status.NoConnection => "No connection",
        Status.ConnectionLost => "Connection lost",
        Status.OperationUnsupported => "Operation unsupported",
        _ => status.ToString(),
    };

    // --- 句柄表 ---

    private sealed class SftpOpenHandle
    {
        internal SftpOpenHandle(Stream? stream, UPath path, bool isDirectory, FileAccess access)
        {
            Stream = stream;
            Path = path;
            IsDirectory = isDirectory;
            Access = access;
        }

        internal Stream? Stream { get; }
        internal UPath Path { get; }
        internal bool IsDirectory { get; }
        internal FileAccess Access { get; }

        /// <summary>目录内容缓存（首次 readdir 时填充）。</summary>
        internal List<SFTPName>? Names { get; set; }

        /// <summary>下一次 readdir 的起始下标。</summary>
        internal int ReadDirIndex { get; set; }

        /// <summary>流位置；不可寻址的流据此做顺序校验。</summary>
        internal long Position { get; set; }
    }

    /// <summary>按句柄上的游标顺序返回目录项，供 JustSFTP 分批消费。</summary>
    private sealed class DirectoryPageEnumerator : IEnumerator<SFTPName>
    {
        private readonly SftpOpenHandle _handle;
        private SFTPName? _current;

        internal DirectoryPageEnumerator(SftpOpenHandle handle) => _handle = handle;

        public SFTPName Current => _current!;

        object System.Collections.IEnumerator.Current => _current!;

        public bool MoveNext()
        {
            var names = _handle.Names;
            if (names is null || _handle.ReadDirIndex >= names.Count)
                return false;
            _current = names[_handle.ReadDirIndex++];
            return true;
        }

        public void Reset() => _handle.ReadDirIndex = 0;

        public void Dispose()
        {
            // 目录句柄的生命周期由 SFTP close 管理。
        }
    }

    private byte[] AddHandle(SftpOpenHandle handle)
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        var key = Convert.ToHexString(bytes);

        lock (_handlesGate)
        {
            _handles[key] = handle;
        }

        return bytes;
    }

    private SftpOpenHandle GetHandle(byte[] handle)
    {
        var key = Convert.ToHexString(handle);
        lock (_handlesGate)
        {
            return _handles.TryGetValue(key, out var entry)
                ? entry
                : throw new HandlerException(Status.Failure, "invalid handle");
        }
    }

    private SftpOpenHandle TakeHandle(byte[] handle)
    {
        var key = Convert.ToHexString(handle);
        lock (_handlesGate)
        {
            if (!_handles.Remove(key, out var entry))
                throw new HandlerException(Status.Failure, "invalid handle");
            return entry;
        }
    }

    /// <summary>释放所有未关闭的句柄。</summary>
    internal void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_handlesGate)
        {
            foreach (var entry in _handles.Values)
                entry.Stream?.Dispose();
            _handles.Clear();
        }
    }
}
