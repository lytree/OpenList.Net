using System.Text;
using SharpCompress.Common;
using SharpCompress.Readers;
using OpenList.Vfs;
using ArchiveEntry = OpenList.Core.Drivers.ArchiveEntry;

namespace OpenList.Drivers.Archive;

/// <summary>
/// 基于 SharpCompress 的归档列表/解压辅助类，作用于 <see cref="IFileSystem"/>，
/// 因此任何驱动（本地、WebDAV、FTP、SFTP…）只需在此委托即可实现
/// <see cref="OpenList.Core.Drivers.IFileSystemArchiveReader"/> /
/// <see cref="OpenList.Core.Drivers.IFileSystemArchiveDecompress"/>。
/// 对应 OpenList Go 在 <c>server/handles/archive.go</c> 中的归档处理。
/// </summary>
internal static class SharpCompressArchiveSupport
{
    /// <summary>
    /// 注册代码页编码（GBK/Shift-JIS 等）并缓存已解析的编码。
    /// </summary>
    /// <remarks>
    /// .NET 默认只带 UTF-8/UTF-16/Latin1，GBK(936) 需要
    /// <see cref="CodePagesEncodingProvider"/>；不注册时 <c>Encoding.GetEncoding("gbk")</c>
    /// 直接抛 <see cref="ArgumentException"/>，设置就成了摆设。
    /// </remarks>
    private static readonly Dictionary<string, Encoding> EncodingCache = new(StringComparer.OrdinalIgnoreCase);

    static SharpCompressArchiveSupport()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* 已注册或平台不支持：保持默认行为，下面的解析会回落到 null */ }
    }

    /// <summary>
    /// 把设置值（编码名，如 <c>gbk</c> / <c>big5</c> / <c>shift_jis</c> 或数字代码页 <c>936</c>）
    /// 解析成 <see cref="Encoding"/>；空值或无法识别返回 <c>null</c>（调用方按默认处理）。
    /// </summary>
    public static Encoding? ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = name.Trim();
        lock (EncodingCache)
        {
            if (EncodingCache.TryGetValue(key, out var cached)) return cached;
        }

        Encoding? encoding = null;
        try { encoding = Encoding.GetEncoding(key); }
        catch (ArgumentException) { /* 未注册/未知编码名：按未设置处理 */ }
        catch (NotSupportedException) { }

        if (encoding is not null)
        {
            lock (EncodingCache) EncodingCache[key] = encoding;
        }
        return encoding;
    }

    /// <summary>
    /// 构造读取选项。<paramref name="entryEncoding"/> 只影响<b>没有</b> UTF-8 标志位的条目名
    /// （<see cref="ArchiveEncoding.Default"/> 的语义），带标志位的 UTF-8 条目不受影响。
    /// </summary>
    private static ReaderOptions BuildOptions(string? password, string? entryEncoding)
    {
        var options = new ReaderOptions
        {
            Password = password ?? string.Empty,
            LeaveStreamOpen = false,
        };
        if (ResolveEncoding(entryEncoding) is { } encoding)
        {
            options.ArchiveEncoding = new ArchiveEncoding { Default = encoding };
        }
        return options;
    }

    /// <summary>
    /// 列出压缩包内的所有条目。
    /// </summary>
    /// <param name="fs">承载压缩包的 虚拟文件系统。</param>
    /// <param name="archivePath">压缩包路径（自动归一化为以 / 开头）。</param>
    /// <param name="password">可选的加密压缩包密码。</param>
    /// <param name="entryEncoding">非 UTF-8 条目名的解码编码（见 <see cref="ResolveEncoding"/>）。</param>
    /// <returns>归档条目列表，目录条目 <c>Size</c> 为 0。</returns>
    public static IReadOnlyList<ArchiveEntry> ListEntries(
        IFileSystem fs, string archivePath, string? password = null, string? entryEncoding = null)
    {
        // 统一路径格式并打开压缩包流。
        var upath = new UPath(archivePath.StartsWith('/') ? archivePath : "/" + archivePath);
        using var stream = fs.OpenFile(upath, FileMode.Open, FileAccess.Read);
        var options = BuildOptions(password, entryEncoding);
        using var reader = ReaderFactory.OpenReader(stream, options);
        var entries = new List<ArchiveEntry>();
        // 逐条读取归档项，归一化路径分隔符并去掉顶层空路径。
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            var name = (entry.Key ?? string.Empty).Replace('\\', '/').Trim('/');
            if (name.Length == 0) continue;
            entries.Add(new ArchiveEntry
            {
                Name = name,
                Size = entry.IsDirectory ? 0 : entry.Size,
                IsDir = entry.IsDirectory,
                Modified = entry.LastModifiedTime.HasValue
                    ? new DateTimeOffset(entry.LastModifiedTime.Value)
                    : DateTimeOffset.MinValue,
            });
        }
        return entries;
    }

    /// <summary>
    /// 打开归档内的单个条目，返回<b>可 Seek</b> 的只读流；找不到返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 压缩流的条目是前向只读的，而内联预览要支持 HTTP Range（视频拖动、文本分段），
    /// 所以先把条目落到临时文件（<see cref="FileOptions.DeleteOnClose"/>，句柄关闭即删），
    /// 再以 <see cref="FileStream"/> 交出去。代价是打开时要把该条目解压一遍 —— 与「先解压再预览」同量级。
    /// </remarks>
    public static Stream? OpenEntry(
        IFileSystem fs, string archivePath, string entryPath, string? password = null, string? entryEncoding = null)
    {
        var target = (entryPath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (target.Length == 0 || IsUnsafePath(target)) return null;

        var upath = new UPath(archivePath.StartsWith('/') ? archivePath : "/" + archivePath);
        using var stream = fs.OpenFile(upath, FileMode.Open, FileAccess.Read);
        var options = BuildOptions(password, entryEncoding);
        using var reader = ReaderFactory.OpenReader(stream, options);
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry.IsDirectory) continue;
            var key = (entry.Key ?? string.Empty).Replace('\\', '/').Trim('/');
            if (!string.Equals(key, target, StringComparison.Ordinal)) continue;

            var temp = Path.Combine(Path.GetTempPath(), "openlist-arc-" + Guid.NewGuid().ToString("N"));
            var file = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.DeleteOnClose);
            try
            {
                using (var entryStream = reader.OpenEntryStream()) entryStream.CopyTo(file);
                // 交给调用方前归零：Read 从头部开始，Media/文本预览才能正常工作。
                file.Position = 0;
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        return null;
    }

    /// <summary>
    /// 将压缩包解压到目标目录（含 ZipSlip 路径穿越防护）。
    /// </summary>
    /// <param name="fs">承载压缩包的 虚拟文件系统。</param>
    /// <param name="archivePath">压缩包路径。</param>
    /// <param name="destPath">解压目标目录（会先去除末尾斜杠）。</param>
    /// <param name="password">可选的加密压缩包密码。</param>
    /// <param name="entryEncoding">非 UTF-8 条目名的解码编码。</param>
    /// <exception cref="InvalidOperationException">归档条目含不安全路径（绝对路径或 <c>..</c> 回退）时抛出。</exception>
    public static void Decompress(
        IFileSystem fs, string archivePath, string destPath, string? password = null, string? entryEncoding = null)
    {
        var upath = new UPath(archivePath.StartsWith('/') ? archivePath : "/" + archivePath);
        var dest = destPath.TrimEnd('/');
        using var stream = fs.OpenFile(upath, FileMode.Open, FileAccess.Read);
        var options = BuildOptions(password, entryEncoding);
        using var reader = ReaderFactory.OpenReader(stream, options);
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry.IsDirectory) continue;

            var entryKey = (entry.Key ?? string.Empty).Replace('\\', '/').Trim('/');
            if (entryKey.Length == 0) continue;
            // 拒绝路径穿越，防止解压到目标目录之外。
            if (IsUnsafePath(entryKey))
                throw new InvalidOperationException($"archive entry has an unsafe path: {entryKey}");

            var target = new UPath(dest.Length == 0 ? "/" + entryKey : dest + "/" + entryKey);
            // 目标父目录不存在时逐层创建。
            var parent = target.GetDirectory();
            if (parent != UPath.Root && !fs.DirectoryExists(parent))
                fs.CreateDirectory(parent);

            // 将压缩项内容流式复制到目标文件。
            using var entryStream = reader.OpenEntryStream();
            using var outStream = fs.OpenFile(target, FileMode.Create, FileAccess.Write);
            entryStream.CopyTo(outStream);
        }
    }

    /// <summary>拒绝路径穿越（ZipSlip）：不允许绝对路径且不允许任何 "/" 分段是 "." 或 ".."。</summary>
    private static bool IsUnsafePath(string key)
    {
        if (key.StartsWith('/') || key.StartsWith('\\')) return true;
        var segments = key.Split('/');
        return segments.Any(s => s == ".." || s == ".");
    }
}
