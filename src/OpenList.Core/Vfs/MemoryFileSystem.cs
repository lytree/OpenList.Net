using System.Collections.Concurrent;

namespace OpenList.Vfs;

/// <summary>
/// 内存文件系统（MemoryFileSystem）。
/// <para>
/// 全内存实现，用于单元测试以及暂不需要协议客户端的驱动（如 SMB 骨架驱动）。
/// 目录结构以路径 → 节点 的并发字典维护：目录节点只记录是否存在，文件节点持有内容与时间戳。
/// 写流为 <see cref="MemoryStream"/>，在 <c>Dispose</c> 时把内容写回内存并更新时间。
/// </para>
/// </summary>
public sealed class MemoryFileSystem : FileSystem
{
    private sealed class MemoryNode
    {
        public bool IsDirectory { get; init; }
        public byte[]? Content { get; set; }
        public DateTime LastWriteTime { get; set; } = DateTime.Now;
        public DateTime CreationTime { get; set; } = DateTime.Now;
        public DateTime LastAccessTime { get; set; } = DateTime.Now;
    }

    private readonly ConcurrentDictionary<string, MemoryNode> _nodes =
        new(StringComparer.Ordinal);

    /// <summary>根节点始终存在。</summary>
    public MemoryFileSystem()
    {
        _nodes["/"] = new MemoryNode { IsDirectory = true };
    }

    /// <summary>清空所有内容（保留根）。</summary>
    public void Clear()
    {
        _nodes.Clear();
        _nodes["/"] = new MemoryNode { IsDirectory = true };
    }

    /// <summary>直接写入文件内容（测试便捷方法）。</summary>
    public void WriteAllBytes(UPath path, byte[] bytes)
    {
        var key = path.ToString();
        EnsureParentDirectory(key);
        _nodes[key] = new MemoryNode
        {
            IsDirectory = false,
            Content = bytes,
            LastWriteTime = DateTime.Now,
            CreationTime = DateTime.Now,
        };
    }

    /// <summary>读取文件内容（测试便捷方法）。</summary>
    public byte[] ReadAllBytes(UPath path)
    {
        if (_nodes.TryGetValue(path.ToString(), out var node) && node.Content is not null)
            return node.Content;
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <summary>逐级确保父目录存在。</summary>
    private void EnsureParentDirectory(string key)
    {
        var idx = key.LastIndexOf('/');
        if (idx <= 0) return;
        var dir = key[..idx];
        EnsureDirectory(dir);
        // 递归向上补齐缺失的祖先。
        var parentIdx = dir.LastIndexOf('/');
        while (parentIdx > 0)
        {
            dir = dir[..parentIdx];
            EnsureDirectory(dir);
            parentIdx = dir.LastIndexOf('/');
        }
    }

    /// <summary>确保某目录节点存在。</summary>
    private void EnsureDirectory(string dir)
    {
        if (dir.Length == 0) dir = "/";
        _nodes.GetOrAdd(dir, _ => new MemoryNode { IsDirectory = true });
    }

    // --- 存在性 ---

    /// <inheritdoc/>
    protected override bool DirectoryExistsImpl(UPath path)
        => _nodes.TryGetValue(path.ToString(), out var node) && node.IsDirectory;

    /// <inheritdoc/>
    protected override bool FileExistsImpl(UPath path)
        => _nodes.TryGetValue(path.ToString(), out var node) && !node.IsDirectory;

    // --- 目录 ---

    /// <inheritdoc/>
    protected override void CreateDirectoryImpl(UPath path)
    {
        var key = path.ToString();
        if (_nodes.TryGetValue(key, out var existing) && existing.IsDirectory) return;
        _nodes[key] = new MemoryNode { IsDirectory = true };
    }

    /// <inheritdoc/>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        var key = path.ToString();
        if (key == "/") return;

        if (isRecursive)
        {
            var prefix = key.TrimEnd('/') + "/";
            foreach (var k in _nodes.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                _nodes.TryRemove(k, out _);
        }

        _nodes.TryRemove(key, out _);
    }

    // --- 文件 ---

    /// <inheritdoc/>
    protected override void DeleteFileImpl(UPath path) => _nodes.TryRemove(path.ToString(), out _);

    /// <inheritdoc/>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var key = path.ToString();
        var canWrite = access is FileAccess.Write or FileAccess.ReadWrite;
        _nodes.TryGetValue(key, out var node);

        if (canWrite)
        {
            switch (mode)
            {
                case FileMode.Open:
                    if (node is null || node.IsDirectory)
                        throw new FileNotFoundException($"Path not found: {path}");
                    break;
                case FileMode.CreateNew:
                    if (node is not null)
                        throw new IOException($"File already exists: {path}");
                    break;
                case FileMode.OpenOrCreate:
                case FileMode.Create:
                case FileMode.Truncate:
                case FileMode.Append:
                    if (node is not null && node.IsDirectory)
                        throw new UnauthorizedAccessException($"Path is a directory: {path}");
                    break;
            }

            EnsureParentDirectory(key);
            // 返回延迟写回的流：关闭时把内容提交到内存节点。
            return new WriteBackStream(this, key, mode, node?.Content);
        }

        if (node is null || node.IsDirectory)
            throw new FileNotFoundException($"Path not found: {path}");

        node.LastAccessTime = DateTime.Now;
        return new MemoryStream(node.Content ?? [], writable: false);
    }

    /// <summary>把内容写回内存节点的流。</summary>
    private sealed class WriteBackStream : MemoryStream
    {
        private readonly MemoryFileSystem _fs;
        private readonly string _key;
        private readonly FileMode _mode;
        private bool _committed;

        public WriteBackStream(MemoryFileSystem fs, string key, FileMode mode, byte[]? existing)
        {
            _fs = fs;
            _key = key;
            _mode = mode;
            // Append 模式从已有内容末尾开始，其余写模式清空缓冲。
            if (mode == FileMode.Append && existing is not null)
                Write(existing, 0, existing.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                // 提交：把当前缓冲内容作为文件新内容。
                var bytes = ToArray();
                _fs._nodes[_key] = new MemoryNode
                {
                    IsDirectory = false,
                    Content = bytes,
                    LastWriteTime = DateTime.Now,
                    CreationTime = _fs._nodes.TryGetValue(_key, out var prev)
                        ? prev.CreationTime
                        : DateTime.Now,
                };
            }

            base.Dispose(disposing);
        }
    }

    /// <inheritdoc/>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        if (!_nodes.TryRemove(srcPath.ToString(), out var node))
            throw new FileNotFoundException($"Path not found: {srcPath}");
        EnsureParentDirectory(destPath.ToString());
        _nodes[destPath.ToString()] = node;
    }

    /// <inheritdoc/>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        var src = srcPath.ToString();
        var dst = destPath.ToString();
        _nodes.TryGetValue(src, out var node);
        if (node is null || !node.IsDirectory)
            throw new DirectoryNotFoundException($"Path not found: {srcPath}");

        EnsureParentDirectory(dst);

        // 先搬迁整个子树：把以 src/ 为前缀的路径重写为 dst/ 前缀。
        var prefix = src.TrimEnd('/') + "/";
        foreach (var key in _nodes.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (_nodes.TryRemove(key, out var child))
                _nodes[dst.TrimEnd('/') + key[src.Length..]] = child;
        }

        // 最后移动目录节点自身；否则源目录节点会残留在内存中。
        _nodes.TryRemove(src, out _);
        _nodes[dst] = node;
    }

    /// <inheritdoc/>
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!overwrite && FileExists(destPath))
            throw new IOException($"File already exists: {destPath}");
        if (!_nodes.TryGetValue(srcPath.ToString(), out var node))
            throw new FileNotFoundException($"Path not found: {srcPath}");

        EnsureParentDirectory(destPath.ToString());
        _nodes[destPath.ToString()] = new MemoryNode
        {
            IsDirectory = false,
            Content = node.Content is null ? [] : (byte[])node.Content.Clone(),
            LastWriteTime = DateTime.Now,
            CreationTime = DateTime.Now,
        };
    }

    /// <inheritdoc/>
    protected override void CopyDirectoryImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        CreateDirectory(destPath);
        foreach (var child in EnumeratePaths(srcPath, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both))
        {
            var target = destPath / child.GetName();
            if (DirectoryExists(child)) CopyDirectory(child, target, overwrite);
            else CopyFile(child, target, overwrite);
        }
    }

    // --- 枚举 ---

    /// <inheritdoc/>
    protected override IEnumerable<UPath> EnumeratePathsImpl(
        UPath path, string searchPattern, SearchDepth searchOption, SearchTarget searchTarget)
    {
        var prefix = path.ToString().TrimEnd('/');
        prefix = prefix.Length == 0 ? "/" : prefix + "/";

        // 只枚举直接子项：键的前缀匹配后，剩余部分不含 '/'。
        foreach (var kvp in _nodes)
        {
            var key = kvp.Key;
            if (key.Length <= prefix.Length) continue;
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = key[prefix.Length..];
            if (searchOption == SearchDepth.TopDirectoryOnly && rest.Contains('/')) continue;

            var childPath = new UPath(prefix + rest);
            var isDir = kvp.Value.IsDirectory;

            if (searchTarget == SearchTarget.File && isDir) continue;
            if (searchTarget == SearchTarget.Directory && !isDir) continue;
            if (!MatchesPattern(childPath.GetName(), searchPattern)) continue;

            yield return childPath;
        }
    }

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
        => _nodes.TryGetValue(path.ToString(), out var node) ? node.Content?.LongLength ?? 0 : 0;

    /// <inheritdoc/>
    protected override DateTime GetLastWriteTimeImpl(UPath path)
        => _nodes.TryGetValue(path.ToString(), out var node) ? node.LastWriteTime : DateTime.MinValue;

    /// <inheritdoc/>
    protected override DateTime GetCreationTimeImpl(UPath path)
        => _nodes.TryGetValue(path.ToString(), out var node) ? node.CreationTime : DateTime.MinValue;

    /// <inheritdoc/>
    protected override DateTime GetLastAccessTimeImpl(UPath path)
        => _nodes.TryGetValue(path.ToString(), out var node) ? node.LastAccessTime : DateTime.MinValue;

    /// <inheritdoc/>
    protected override void SetLastWriteTimeImpl(UPath path, DateTime time)
    {
        if (_nodes.TryGetValue(path.ToString(), out var node)) node.LastWriteTime = time;
    }

    /// <inheritdoc/>
    protected override void SetCreationTimeImpl(UPath path, DateTime time)
    {
        if (_nodes.TryGetValue(path.ToString(), out var node)) node.CreationTime = time;
    }
}
