using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.Cloud.AliyundriveOpen;

/// <summary>
/// 阿里云盘在 Zio 挂载层上的视图。
/// <para>
/// 与 S3 / WebDAV 不同，阿里云盘的 API 完全按 <c>file_id</c> 寻址，路径只是展示形式。
/// 因此本类承担 <b>路径 → 对象</b> 的解析：从配置的根 id 出发逐级列目录下钻，
/// 命中结果进 <see cref="_pathCache"/>，使后续访问不再重复下钻。
/// </para>
/// <para>
/// 解析结果是 <see cref="AliyunFile"/>（含 id/size/时间），驱动层的列目录、取链接、
/// 增删改一律基于它，避免同一路径被反复解析。
/// </para>
/// <para>
/// 所有 Zio 原语都是同步签名，实现照例用 <c>GetAwaiter().GetResult()</c> 桥接异步调用
/// （与 <c>WebDavFileSystem</c> 一致）。
/// </para>
/// </summary>
internal sealed class AliyundriveOpenFileSystem : NetworkFileSystemBase
{
    private readonly AliyundriveOpenDriver _driver;

    /// <summary>路径 → 云盘对象缓存（键为规范化后的绝对路径，如 <c>/dir/a.txt</c>）。</summary>
    private readonly ConcurrentDictionary<string, AliyunFile> _pathCache = new(StringComparer.Ordinal);

    internal AliyundriveOpenFileSystem(AliyundriveOpenDriver driver) => _driver = driver;

    /// <summary>把 Zio 路径规范化成缓存键（去掉末尾斜杠；根路径统一为 <c>/</c>）。</summary>
    private static string Key(UPath path)
    {
        var full = path.FullName;
        if (string.IsNullOrEmpty(full)) return "/";
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }

    /// <summary>
    /// 把路径解析为云盘对象；根路径返回 null（它由配置的 <c>root_folder_id</c> 表示，
    /// 本身不是云盘上的一个条目）。
    /// </summary>
    /// <param name="path">Zio 路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中时返回对象，路径不存在时返回 null。</returns>
    internal async Task<AliyunFile?> ResolveAsync(UPath path, CancellationToken ct = default)
    {
        var key = Key(path);
        if (key == "/") return null;
        if (_pathCache.TryGetValue(key, out var cached)) return cached;

        // 缓存未命中：从根 id 起逐级列目录。每级至多一次 list 请求（受 limiterList 限流），
        // 沿途命中的中间目录一并写入缓存，后续同前缀访问即可短路。
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentId = _driver.RootFolderId;
        AliyunFile? current = null;
        var walked = string.Empty;

        foreach (var segment in segments)
        {
            var children = await _driver.GetFilesAsync(parentId, ct).ConfigureAwait(false);
            var match = children.FirstOrDefault(
                f => string.Equals(f.EffectiveName, segment, StringComparison.Ordinal));
            if (match is null) return null;

            walked += "/" + segment;
            _pathCache[walked] = match;
            current = match;
            parentId = match.FileId ?? string.Empty;
        }

        return current;
    }

    /// <summary>
    /// 路径本身及其全部子路径失效（写操作后调用，避免缓存指向已被移动/删除的 id）。
    /// 上传等由驱动直接发起的写操作也会调用它。
    /// </summary>
    internal void InvalidateDirectory(UPath path)
    {
        var key = Key(path);
        foreach (var cached in _pathCache.Keys)
        {
            if (cached.Equals(key, StringComparison.Ordinal) ||
                cached.StartsWith(key + "/", StringComparison.Ordinal))
            {
                _pathCache.TryRemove(cached, out _);
            }
        }
    }

    // --- 存在性 ---

    /// <summary>根路径恒存在；其余按解析结果是否为目录判定。</summary>
    protected override bool DirectoryExistsImpl(UPath path)
        => Key(path) == "/" || ResolveAsync(path).GetAwaiter().GetResult() is { IsFolder: true };

    /// <summary>文件存在性 = 解析到对象且不是目录。</summary>
    protected override bool FileExistsImpl(UPath path)
        => ResolveAsync(path).GetAwaiter().GetResult() is { IsFolder: false };

    // --- 目录操作 ---

    /// <summary>创建目录：在父目录下调 <c>openFile/create</c>（type=folder、重名拒绝）。</summary>
    protected override void CreateDirectoryImpl(UPath path)
    {
        var parent = ResolveAsync(path.GetDirectory()).GetAwaiter().GetResult();
        var parentId = parent?.FileId ?? _driver.RootFolderId;
        var created = _driver.MakeDirAsync(parentId, path.GetName()).GetAwaiter().GetResult();
        _pathCache[Key(path)] = created;
    }

    /// <summary>
    /// 删除目录。阿里云盘删除目录时子树一并进回收站（或直接被删除，取决于 <c>remove_way</c>），
    /// 因此无需在本地递归枚举逐个删除。
    /// </summary>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive) => DeleteEntry(path);

    // --- 文件操作 ---

    /// <summary>删除文件。</summary>
    protected override void DeleteFileImpl(UPath path) => DeleteEntry(path);

    /// <summary>删除公共路径：解析对象后交给驱动按下发配置删除。</summary>
    private void DeleteEntry(UPath path)
    {
        var file = ResolveAsync(path).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException($"Path not found: {path}");
        _driver.RemoveAsync(file).GetAwaiter().GetResult();
        InvalidateDirectory(path);
    }

    /// <summary>移动文件（跨目录或改名）。</summary>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath) => MoveEntry(srcPath, destPath);

    /// <summary>移动目录 —— 云盘的 move 对目录同样适用，无需「先复制再删除」的默认实现。</summary>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath) => MoveEntry(srcPath, destPath);

    /// <summary>
    /// 移动公共路径。云盘 <c>openFile/move</c> 只改父目录，因此目标名与源名不同时
    /// 需要再补一次 <c>openFile/update</c> 改名（Zio 的移动语义包含改名）。
    /// </summary>
    private void MoveEntry(UPath srcPath, UPath destPath)
    {
        var src = ResolveAsync(srcPath).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException($"Path not found: {srcPath}");
        var destParent = ResolveAsync(destPath.GetDirectory()).GetAwaiter().GetResult();
        var parentId = destParent?.FileId ?? _driver.RootFolderId;
        var destName = destPath.GetName();

        _driver.MoveAsync(src, parentId, destName).GetAwaiter().GetResult();

        InvalidateDirectory(srcPath);
        InvalidateDirectory(destPath);
    }

    /// <summary>复制文件：直接用云盘原生 copy（服务端复制，不经过本地流量）。</summary>
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!overwrite && FileExists(destPath))
            throw new IOException($"File already exists: {destPath}");

        var src = ResolveAsync(srcPath).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException($"Path not found: {srcPath}");
        var destParent = ResolveAsync(destPath.GetDirectory()).GetAwaiter().GetResult();
        var parentId = destParent?.FileId ?? _driver.RootFolderId;

        _driver.CopyAsync(src, parentId, destPath.GetName()).GetAwaiter().GetResult();
        InvalidateDirectory(destPath);
    }

    // --- 打开文件 ---

    /// <summary>
    /// 读模式：取下载直链后由共享 <see cref="System.Net.Http.HttpClient"/> 拉流。
    /// 写模式不支持 —— 上传统一走 <c>IFileSystemUploader</c>（云盘上传需要分片/秒传，
    /// 不是「写一个流」能表达的语义），见 <c>AliyundriveOpenDriver.PutAsync</c>。
    /// </summary>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        if (access is FileAccess.Write or FileAccess.ReadWrite)
            throw new NotSupportedException(
                "AliyundriveOpen does not support stream writes; use IFileSystemUploader.PutAsync instead.");

        return _driver.OpenReadStreamAsync(path.FullName, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    // --- 元数据 ---

    /// <summary>文件大小取自 list/get 响应。</summary>
    protected override long GetFileLengthImpl(UPath path)
        => ResolveAsync(path).GetAwaiter().GetResult()?.Size ?? 0;

    /// <summary>最后修改时间 = 云盘的 <c>updated_at</c>。</summary>
    protected override DateTime GetLastWriteTimeImpl(UPath path)
        => (ResolveAsync(path).GetAwaiter().GetResult()?.UpdatedAt ?? DateTimeOffset.MinValue).LocalDateTime;

    /// <summary>创建时间 = 云盘的 <c>created_at</c>。</summary>
    protected override DateTime GetCreationTimeImpl(UPath path)
        => (ResolveAsync(path).GetAwaiter().GetResult()?.CreatedAt ?? DateTimeOffset.MinValue).LocalDateTime;

    // --- 枚举 ---

    /// <summary>
    /// 枚举子项：一次 list 拉取整页（200 条/页，驱动内部翻页），
    /// 顺带预热 <see cref="_pathCache"/>，使随后按子路径访问无需再次下钻。
    /// </summary>
    protected override IEnumerable<UPath> EnumeratePathsImpl(
        UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        var dir = ResolveAsync(path).GetAwaiter().GetResult();
        var folderId = dir?.FileId ?? _driver.RootFolderId;
        var children = _driver.GetFilesAsync(folderId).GetAwaiter().GetResult();

        foreach (var child in children)
        {
            var name = child.EffectiveName;
            if (string.IsNullOrEmpty(name) || !MatchesPattern(name, searchPattern)) continue;

            var childPath = path / name;
            var isDir = child.IsFolder;
            _pathCache[Key(childPath)] = child;

            if (searchTarget is SearchTarget.Both
                || (searchTarget == SearchTarget.Directory && isDir)
                || (searchTarget == SearchTarget.File && !isDir))
            {
                yield return childPath;
            }

            if (searchOption == SearchOption.AllDirectories && isDir)
            {
                foreach (var sub in EnumeratePathsImpl(childPath, searchPattern, searchOption, searchTarget))
                    yield return sub;
            }
        }
    }

    /// <summary>通配符匹配（<c>*</c> 任意多字符、<c>?</c> 单字符），与 <c>WebDavFileSystem</c> 同实现。</summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}
