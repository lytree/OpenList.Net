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
/// </summary>
public sealed class MountFileSystem : FileSystem
{
    /// <summary>挂载路径 → 子文件系统。</summary>
    private readonly ConcurrentDictionary<string, IFileSystem> _mounts =
        new(StringComparer.OrdinalIgnoreCase);

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

    // --- 存在性 ---

    /// <inheritdoc/>
    protected override bool DirectoryExistsImpl(UPath path)
        => TryTranslate(path, out var fs, out var rel) ? fs.DirectoryExists(rel) : false;

    /// <inheritdoc/>
    protected override bool FileExistsImpl(UPath path)
        => TryTranslate(path, out var fs, out var rel) && fs.FileExists(rel);

    // --- 目录 ---

    /// <inheritdoc/>
    protected override void CreateDirectoryImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) fs.CreateDirectory(rel);
    }

    /// <inheritdoc/>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        if (TryTranslate(path, out var fs, out var rel)) fs.DeleteDirectory(rel, isRecursive);
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
        // 未挂载任何文件系统时，根路径的枚举结果就是「各挂载点本身」，
        // 这样前端才能在没有子文件系统提供列表时仍看到存储列表。
        if (!TryTranslate(path, out var fs, out var rel))
        {
            foreach (var mount in EnumerateMounts())
            {
                if (searchTarget == SearchTarget.File) continue;
                if (MatchesPattern(mount.GetName(), searchPattern))
                    yield return mount;
            }

            yield break;
        }

        foreach (var child in fs.EnumeratePaths(rel, searchPattern, searchOption, searchTarget))
        {
            yield return path == UPath.Root ? child : path / child.GetName();
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
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <inheritdoc/>
    protected override DateTime GetCreationTimeImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) return fs.GetCreationTime(rel);
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <inheritdoc/>
    protected override DateTime GetLastAccessTimeImpl(UPath path)
    {
        if (TryTranslate(path, out var fs, out var rel)) return fs.GetLastAccessTime(rel);
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
