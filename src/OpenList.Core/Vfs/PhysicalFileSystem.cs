using System.Runtime.InteropServices;

namespace OpenList.Vfs;

/// <summary>
/// 本地物理文件系统（PhysicalFileSystem）。
/// <para>
/// 承担双向路径映射：把 VFS 的 <c>/a/b</c> 风格路径与操作系统本地路径
/// （Windows 下的 <c>C:\a\b</c>、Unix 下的 <c>/a/b</c>）互转。
/// Windows 上盘符被映射为第一级路径段（<c>C:\data</c> → <c>/C:/data</c>），
/// 因此 <see cref="SubFileSystem"/> 能以它为底层把某个本地目录安全地当作挂载根。
/// </para>
/// </summary>
public sealed class PhysicalFileSystem : FileSystem
{
    private readonly bool _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>是否为 Windows 平台。</summary>
    public bool IsWindows => _isWindows;

    /// <inheritdoc/>
    public override bool IsCaseSensitive => !_isWindows;

    /// <summary>
    /// 把操作系统本地路径转换为 VFS 路径。
    /// <para>
    /// Windows：<c>C:\data\x</c> → <c>/C:/data/x</c>；UNC <c>\\server\share</c> → <c>//server/share</c>。
    /// Unix：<c>/data/x</c> → <c>/data/x</c>。
    /// </para>
    /// </summary>
    public string ConvertPathFromInternalToString(string nativePath)
    {
        if (string.IsNullOrEmpty(nativePath)) return "/";
        var p = nativePath.Replace('\\', '/');

        if (_isWindows)
        {
            // UNC 路径：\\server\share → /server/share
            if (p.StartsWith("//", StringComparison.Ordinal)) return new UPath(p).ToString();

            if (p.Length >= 2 && p[1] == ':')
            {
                // 盘符路径：C:\data → /C:/data
                return new UPath("/" + p).ToString();
            }

            return new UPath(p).ToString();
        }

        return new UPath(p).ToString();
    }

    /// <summary>
    /// 把 VFS 路径转换为操作系统本地路径（<see cref="ConvertPathFromInternalToString"/> 的逆操作）。
    /// </summary>
    public string ConvertPathToInternalToString(UPath path)
    {
        var full = path.ToString();
        if (string.IsNullOrEmpty(full) || full == "/") return _isWindows ? "/" : "/";

        var trimmed = full.TrimStart('/');

        if (_isWindows)
        {
            // /C:/data → C:\data
            if (trimmed.Length >= 2 && trimmed[1] == ':')
            {
                var drive = char.ToUpperInvariant(trimmed[0]);
                // 从第 3 个字符（冒号之后）起取剩余部分。
                var rest = trimmed.Length > 2 ? trimmed[2..].TrimStart('/').Replace('/', '\\') : string.Empty;
                return rest.Length == 0 ? $"{drive}:\\" : $"{drive}:\\{rest}";
            }

            // //server/share → \\server\share
            if (full.StartsWith("//", StringComparison.Ordinal))
                return full.Replace('/', '\\');

            return trimmed.Replace('/', '\\');
        }

        return "/" + trimmed;
    }

    /// <inheritdoc/>
    protected override UPath ConvertPathFromInternalImpl(string internalPath)
        => new(ConvertPathFromInternalToString(internalPath));

    /// <inheritdoc/>
    protected override string ConvertPathToInternalImpl(UPath path)
        => ConvertPathToInternalToString(path);

    // --- 存在性 ---

    /// <inheritdoc/>
    protected override bool DirectoryExistsImpl(UPath path)
        => Directory.Exists(ConvertPathToInternalImpl(path));

    /// <inheritdoc/>
    protected override bool FileExistsImpl(UPath path)
        => File.Exists(ConvertPathToInternalImpl(path));

    // --- 目录 ---

    /// <inheritdoc/>
    protected override void CreateDirectoryImpl(UPath path)
        => Directory.CreateDirectory(ConvertPathToInternalImpl(path));

    /// <inheritdoc/>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        var target = ConvertPathToInternalImpl(path);
        if (isRecursive)
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
                return;
            }

            // 路径不存在时基类已做存在性校验，此处仅兜底。
            return;
        }

        Directory.Delete(target, recursive: false);
    }

    // --- 文件 ---

    /// <inheritdoc/>
    protected override void DeleteFileImpl(UPath path)
    {
        var target = ConvertPathToInternalImpl(path);
        if (File.Exists(target)) File.Delete(target);
    }

    /// <inheritdoc/>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var target = ConvertPathToInternalImpl(path);
        // 目录一律拒绝按文件打开，避免误用。
        if (mode is FileMode.Open or FileMode.OpenOrCreate or FileMode.Truncate or FileMode.Append
            && Directory.Exists(target))
            throw new UnauthorizedAccessException($"Path is a directory: {path}");

        return new FileStream(
            target,
            mode,
            access,
            share == FileShare.None ? FileShare.None : share,
            bufferSize: 4096,
            useAsync: false);
    }

    /// <inheritdoc/>
    protected override IEnumerable<UPath> EnumeratePathsImpl(
        UPath path, string searchPattern, SearchDepth searchOption, SearchTarget searchTarget)
    {
        var target = ConvertPathToInternalImpl(path);
        // 本类型与 System.IO.SearchOption 同名，此处显式限定 BCL 枚举。
        var option = searchOption == SearchDepth.AllDirectories
            ? System.IO.SearchOption.AllDirectories
            : System.IO.SearchOption.TopDirectoryOnly;

        // 用通配枚举避免把目录与文件拆成两次 IO；再按 searchTarget 过滤。
        var entries = Directory.EnumerateFileSystemEntries(target, "*", option);
        foreach (var native in entries)
        {
            var childPath = ConvertPathFromInternalImpl(native);
            var isDir = Directory.Exists(native);

            if (searchTarget == SearchTarget.File && isDir) continue;
            if (searchTarget == SearchTarget.Directory && !isDir) continue;
            if (!MatchesPattern(childPath.GetName(), searchPattern)) continue;

            yield return childPath;
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
        => new FileInfo(ConvertPathToInternalImpl(path)).Length;

    /// <inheritdoc/>
    protected override DateTime GetLastWriteTimeImpl(UPath path)
        => File.GetLastWriteTime(ConvertPathToInternalImpl(path));

    /// <inheritdoc/>
    protected override DateTime GetCreationTimeImpl(UPath path)
    {
        var native = ConvertPathToInternalImpl(path);
        return Directory.Exists(native)
            ? Directory.GetCreationTime(native)
            : File.GetCreationTime(native);
    }

    /// <inheritdoc/>
    protected override DateTime GetLastAccessTimeImpl(UPath path)
        => File.GetLastAccessTime(ConvertPathToInternalImpl(path));

    /// <inheritdoc/>
    protected override void SetLastWriteTimeImpl(UPath path, DateTime time)
        => File.SetLastWriteTime(ConvertPathToInternalImpl(path), time);

    /// <inheritdoc/>
    protected override void SetCreationTimeImpl(UPath path, DateTime time)
        => File.SetCreationTime(ConvertPathToInternalImpl(path), time);

    /// <inheritdoc/>
    protected override FileAttributes GetAttributesImpl(UPath path)
        => File.GetAttributes(ConvertPathToInternalImpl(path));

    /// <inheritdoc/>
    protected override void SetAttributesImpl(UPath path, FileAttributes attributes)
        => File.SetAttributes(ConvertPathToInternalImpl(path), attributes);
}
