using System.Collections.Concurrent;

namespace OpenList.Vfs;

/// <summary>
/// 挂载点路由文件系统（MountFileSystem）。
/// <para>
/// 自身不存储任何数据，而是把以 <c>/</c> 开头的路径按<b>最长前缀匹配</b>路由到已挂载的
/// 子文件系统：<c>/s3/bucket/a.txt</c> 若 <c>/s3</c> 处挂载了某个驱动文件系统，则以
/// <c>/bucket/a.txt</c> 转发给它。所有相对路径的元数据与流操作都经由子文件系统完成。
/// </para>
/// <para>
/// 挂载表使用并发字典，且读取路径前缀时取<b>最长匹配</b>，
/// 以正确支持 <c>/a</c> 与 <c>/a/b</c> 同时挂载的场景。
/// </para>
/// <para>
/// <b>虚拟目录</b>：根目录 <c>/</c> 以及各挂载点的祖先目录（例如只挂了 <c>/a/b</c> 时的 <c>/a</c>）
/// 不对应任何子文件系统，但它们在逻辑上必然存在 —— 否则无法逐层进入深层挂载点。这类路径的
/// 存在性、子项枚举与元数据都由本类就地合成，不做任何转发。
/// <b>注意</b>：基类把「根目录恒存在」写死在 <see cref="FileSystem.DirectoryExists"/> 与
/// <see cref="FileSystem.ValidatePath"/> 里，因此这里的元数据实现必须同样兜住虚拟目录，
/// 否则 <c>GetDirectoryEntry("/")</c> 会直接抛 <see cref="FileNotFoundException"/>。
/// </para>
/// </summary>
public sealed class MountFileSystem : FileSystem
{
    /// <summary>挂载路径 → 子文件系统。</summary>
    private readonly ConcurrentDictionary<string, IFileSystem> _mounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 虚拟目录的时间戳占位值。虚拟目录不对应任何真实存储，没有可查询的修改/创建时间，
    /// 统一返回「无时间信息」的纪元时间，而不是抛异常或返回 <c>DateTime.Now</c>（后者会让
    /// 每次请求的值都不同，干扰前端缓存与增量索引比对）。
    /// </summary>
    public static readonly DateTime VirtualDirectoryTime = DateTime.UnixEpoch;

    /// <inheritdoc/>
    public override bool IsCaseSensitive => false;

    /// <summary>已挂载的挂载路径（不含根）。</summary>
    public IEnumerable<UPath> MountedPaths => _mounts.Keys.Select(p => new UPath(p));

    /// <summary>按挂载路径挂载子文件系统；同一路径已挂载时替换旧的。</summary>
    /// <param name="path">挂载路径。</param>
    /// <param name="fileSystem">要挂载的文件系统。</param>
    public void Mount(UPath path, IFileSystem fileSystem)
        => _mounts[path.ToString()] = fileSystem;

    /// <summary>按字符串挂载路径挂载子文件系统（便捷重载）。</summary>
    /// <param name="path">挂载路径字符串，相对路径视为相对于根。</param>
    /// <param name="fileSystem">要挂载的文件系统。</param>
    public void Mount(string path, IFileSystem fileSystem) => Mount(new UPath(path), fileSystem);

    /// <summary>卸载指定路径的子文件系统（不释放其资源）。</summary>
    /// <param name="path">挂载路径。</param>
    /// <returns>成功卸载返回 true。</returns>
    public bool Unmount(UPath path) => _mounts.TryRemove(path.ToString(), out _);

    /// <summary>
    /// 找到能容纳 <paramref name="path"/> 的挂载点（最长前缀匹配）。
    /// </summary>
    /// <param name="path">待解析路径。</param>
    /// <param name="mountedPath">匹配到的挂载路径。</param>
    /// <param name="fileSystem">对应的子文件系统。</param>
    /// <returns>存在匹配的挂载点返回 true。</returns>
    public bool TryResolve(UPath path, out UPath mountedPath, out IFileSystem fileSystem)
    {
        var full = path.ToString();
        if (_mounts.TryGetValue(full, out var exact))
        {
            mountedPath = path;
            fileSystem = exact;
            return true;
        }

        // 最长前缀匹配：逐级截短路径，找到第一个已挂载的祖先。
        var best = default(KeyValuePair<string, IFileSystem>);
        var found = false;
        var current = full;
        while (current.Length > 0)
        {
            if (_mounts.TryGetValue(current, out var fs))
            {
                best = new KeyValuePair<string, IFileSystem>(current, fs);
                found = true;
                break;
            }

            var slash = current.LastIndexOf('/');
            if (slash <= 0) break;
            current = current[..slash];
        }

        if (found)
        {
            mountedPath = new UPath(best.Key);
            fileSystem = best.Value;
            return true;
        }

        mountedPath = UPath.Root;
        fileSystem = null!;
        return false;
    }

    /// <summary>
    /// 把路径翻译为「目标文件系统 + 该文件系统内的相对路径」。
    /// </summary>
    private bool TryTranslate(UPath path, out IFileSystem fs, out UPath relative)
    {
        if (TryResolve(path, out var mounted, out fs))
        {
            var mountName = mounted.ToString();
            var full = path.ToString();

            if (string.Equals(mountName, full, StringComparison.OrdinalIgnoreCase))
            {
                relative = UPath.Root;
            }
            else
            {
                var rel = full.Length > mountName.Length ? full[mountName.Length..] : string.Empty;
                relative = new UPath(rel.Length == 0 ? "/" : rel);
            }

            return true;
        }

        fs = null!;
        relative = UPath.Root;
        return false;
    }

    /// <summary>按挂载点枚举子路径（用于列出所有挂载存储）。</summary>
    public IEnumerable<UPath> EnumerateMounts() => MountedPaths;

    // --- 虚拟目录 ---

    /// <summary>
    /// 判断路径是否为虚拟目录：根目录，或某个挂载点的祖先目录（如挂载 <c>/a/b</c> 时的 <c>/a</c>）。
    /// </summary>
    /// <param name="path">待判断的路径。</param>
    /// <returns>是虚拟目录返回 true。</returns>
    public bool IsVirtualDirectory(UPath path)
    {
        if (path.IsNull) return false;
        var full = path.ToString();
        if (full == "/") return true;

        foreach (var key in _mounts.Keys)
        {
            // 必须是「整段前缀」：/a 是 /a/b 的祖先，但不是 /ab 的祖先。
            if (key.Length > full.Length &&
                key.StartsWith(full, StringComparison.OrdinalIgnoreCase) &&
                key[full.Length] == '/')
                return true;
        }

        return false;
    }

    /// <summary>
    /// 枚举虚拟目录的「下一级名称」：把所有落在其下的挂载路径截到下一段并去重。
    /// 例如挂载了 <c>/a/b</c> 与 <c>/c</c> 时，根目录得到 <c>a</c>、<c>c</c>；<c>/a</c> 得到 <c>b</c>。
    /// </summary>
    /// <param name="path">虚拟目录路径。</param>
    /// <returns>按名称排序的子目录名。</returns>
    private List<string> EnumerateVirtualChildNames(UPath path)
    {
        var full = path.ToString();
        var prefix = full == "/" ? "/" : full + "/";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _mounts.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = key[prefix.Length..];
            if (rest.Length == 0) continue;
            var slash = rest.IndexOf('/');
            var name = slash < 0 ? rest : rest[..slash];
            if (name.Length > 0) names.Add(name);
        }

        // 稳定排序：并发字典的键顺序不确定，直接透出会让列表顺序随机。
        var list = names.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // --- 存在性 ---

    /// <inheritdoc/>
    protected override bool DirectoryExistsImpl(UPath path)
        => TryTranslate(path, out var fs, out var rel) ? fs.DirectoryExists(rel) : IsVirtualDirectory(path);

    /// <inheritdoc/>
    protected override bool FileExistsImpl(UPath path)
        => TryTranslate(path, out var fs, out var rel) && fs.FileExists(rel);

    // --- 目录 ---

    /// <inheritdoc/>
    protected override void CreateDirectoryImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) { fs.CreateDirectory(rel); return; }
        // 虚拟目录由挂载表推导得出，不可能被「创建」；静默成功会让调用方误以为建成功了。
        if (IsVirtualDirectory(path)) throw new NotSupportedException($"Virtual directory is read-only: {path}");
    }

    /// <inheritdoc/>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        if (TryTranslate(path, out var fs, out var rel)) { fs.DeleteDirectory(rel, isRecursive); return; }
        if (IsVirtualDirectory(path)) throw new NotSupportedException($"Virtual directory is read-only: {path}");
    }

    /// <inheritdoc/>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        // 跨挂载点的移动不被支持：需要两个路径落在同一子文件系统内。
        if (!TryTranslate(srcPath, out var srcFs, out var srcRel) ||
            !TryTranslate(destPath, out var dstFs, out var dstRel))
            throw new IOException("Directory not found");

        if (!ReferenceEquals(srcFs, dstFs))
            throw new NotSupportedException("Cross-mount move is not supported.");
        srcFs.MoveDirectory(srcRel, dstRel);
    }

    /// <inheritdoc/>
    protected override void CopyDirectoryImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!TryTranslate(srcPath, out var srcFs, out var srcRel) ||
            !TryTranslate(destPath, out var dstFs, out var dstRel))
            throw new DirectoryNotFoundException($"Path not found: {srcPath}");

        if (!ReferenceEquals(srcFs, dstFs))
            throw new NotSupportedException("Cross-mount copy is not supported.");
        srcFs.CopyDirectory(srcRel, dstRel, overwrite);
    }

    // --- 文件 ---

    /// <inheritdoc/>
    protected override void DeleteFileImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) fs.DeleteFile(rel);
    }

    /// <inheritdoc/>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        if (!TryTranslate(srcPath, out var srcFs, out var srcRel) ||
            !TryTranslate(destPath, out var dstFs, out var dstRel))
            throw new FileNotFoundException($"Path not found: {srcPath}");

        if (!ReferenceEquals(srcFs, dstFs))
            throw new NotSupportedException("Cross-mount move is not supported.");
        srcFs.MoveFile(srcRel, dstRel);
    }

    /// <inheritdoc/>
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!TryTranslate(srcPath, out var srcFs, out var srcRel) ||
            !TryTranslate(destPath, out var dstFs, out var dstRel))
            throw new FileNotFoundException($"Path not found: {srcPath}");

        if (!ReferenceEquals(srcFs, dstFs))
            throw new NotSupportedException("Cross-mount copy is not supported.");
        srcFs.CopyFile(srcRel, dstRel, overwrite);
    }

    /// <inheritdoc/>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        if (TryTranslate(path, out var fs, out var rel)) return fs.OpenFile(rel, mode, access, share);
        throw new FileNotFoundException($"Path not found: {path}");
    }

    // --- 枚举 ---

    /// <inheritdoc/>
    protected override IEnumerable<UPath> EnumeratePathsImpl(
        UPath path, string searchPattern, SearchDepth searchOption, SearchTarget searchTarget)
    {
        // 虚拟目录（根目录、挂载点的祖先）：内容就是「下一级挂载路径段」。
        // 否则前端在只挂了 /a/b 时既看不到 a，也进不去 /a。
        if (!TryTranslate(path, out var fs, out var rel))
        {
            if (searchTarget == SearchTarget.File) yield break;
            foreach (var name in EnumerateVirtualChildNames(path))
            {
                if (MatchesPattern(name, searchPattern))
                    yield return path.Combine(name);
            }

            yield break;
        }

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in fs.EnumeratePaths(rel, searchPattern, searchOption, searchTarget))
        {
            var item = path == UPath.Root ? child : path.Combine(child.GetName());
            if (emitted.Add(item.GetName()))
                yield return item;
        }

        // 子挂载点也要补进列表：/a 处已有存储、又在其下挂了 /a/b 时，
        // 上面的枚举来自 /a 的存储，看不到 /a/b 这个挂载点本身。
        // 只在顶层枚举时补充 —— 递归跨挂载点遍历的成本与语义都不在此处定义。
        if (searchTarget == SearchTarget.File || searchOption != SearchDepth.TopDirectoryOnly)
            yield break;

        foreach (var name in EnumerateVirtualChildNames(path))
        {
            if (MatchesPattern(name, searchPattern) && emitted.Add(name))
                yield return path.Combine(name);
        }
    }

    /// <summary>通配符匹配（<c>*</c> 任意多字符、<c>?</c> 单字符）。</summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // --- 元数据 ---

    /// <inheritdoc/>
    protected override long GetFileLengthImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) return fs.GetFileLength(rel);
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <inheritdoc/>
    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) return fs.GetLastWriteTime(rel);
        // 虚拟目录没有真实时间戳，返回占位值而不是抛异常：
        // 基类已把「根路径恒存在」当成契约，这里必须一致。
        if (IsVirtualDirectory(path)) return VirtualDirectoryTime;
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <inheritdoc/>
    protected override DateTime GetCreationTimeImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) return fs.GetCreationTime(rel);
        if (IsVirtualDirectory(path)) return VirtualDirectoryTime;
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <inheritdoc/>
    protected override DateTime GetLastAccessTimeImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) return fs.GetLastAccessTime(rel);
        if (IsVirtualDirectory(path)) return VirtualDirectoryTime;
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <inheritdoc/>
    protected override void SetLastWriteTimeImpl(UPath path, DateTime time)
    {
        if (TryTranslate(path, out var fs, out var rel)) fs.SetLastWriteTime(rel, time);
    }

    /// <inheritdoc/>
    protected override void SetCreationTimeImpl(UPath path, DateTime time)
    {
        if (TryTranslate(path, out var fs, out var rel)) fs.SetCreationTime(rel, time);
    }

    /// <inheritdoc/>
    protected override FileAttributes GetAttributesImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) return fs.GetAttributes(rel);
        if (IsVirtualDirectory(path)) return FileAttributes.Directory;
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <inheritdoc/>
    protected override void SetAttributesImpl(UPath path, FileAttributes attributes)
    {
        if (TryTranslate(path, out var fs, out var rel)) fs.SetAttributes(rel, attributes);
    }

    /// <summary>释放所有已挂载的子文件系统。</summary>
    public override void Dispose()
    {
        foreach (var kvp in _mounts)
        {
            try { kvp.Value.Dispose(); } catch { /* 释放失败不影响其余 */ }
        }

        _mounts.Clear();
        base.Dispose();
    }
}
