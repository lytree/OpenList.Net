using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using OpenList.Vfs;

namespace OpenList.Drivers.S3;

/// <summary>
/// 基于 S3 兼容存储桶（Minio、AWS S3 等）的 <see cref="FileSystem"/>。
/// S3 是对象存储：所谓"目录"其实是对象键的前缀，"文件"即对象本身。
/// </summary>
internal sealed class S3FileSystem : NetworkFileSystemBase
{
    private readonly MinioClient _client;
    private readonly string _bucket;
    private readonly string _rootPrefix;

    /// <summary>构造并绑定到指定存储桶，rootPrefix 归一化为以 "/" 结尾的根前缀。</summary>
    public S3FileSystem(MinioClient client, string bucket, string rootPrefix = "")
    {
        _client = client;
        _bucket = bucket;
        _rootPrefix = string.IsNullOrEmpty(rootPrefix) ? "" :
            (rootPrefix.EndsWith('/') ? rootPrefix : rootPrefix + "/");
    }

    // --- 路径辅助 ---

    /// <summary>把 VFS 路径转换为 S3 对象键（拼接根前缀）。</summary>
    private string ToKey(UPath path)
    {
        var relative = path.FullName.TrimStart('/');
        return string.IsNullOrEmpty(relative) ? _rootPrefix : _rootPrefix + relative;
    }

    /// <summary>把 S3 对象键去掉根前缀，转换回 VFS 路径。</summary>
    private UPath FromKey(string key)
    {
        var relative = key.StartsWith(_rootPrefix, StringComparison.Ordinal)
            ? key[_rootPrefix.Length..]
            : key;
        return UPath.Root / relative;
    }

    // --- 核心覆写 ---

    /// <summary>目录存在性 = 该前缀下至少有对象（S3 无真实目录）。</summary>
    protected override bool DirectoryExistsImpl(UPath path)
    {
        // 根路径恒视为存在；其余目录通过"前缀下是否有对象"判定。
        if (path == UPath.Root) return true;
        var prefix = ToKey(path).TrimEnd('/') + "/";
        return ListObjectsSync(prefix, recursive: false).Any();
    }

    /// <summary>文件存在性 = 执行 StatObject 成功。</summary>
    protected override bool FileExistsImpl(UPath path)
    {
        try
        {
            _client.StatObjectAsync(new StatObjectArgs()
                .WithBucket(_bucket)
                .WithObject(ToKey(path))).GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }

    /// <summary>文件长度 = 对象统计信息中的 Size。</summary>
    protected override long GetFileLengthImpl(UPath path)
    {
        var stat = _client.StatObjectAsync(new StatObjectArgs()
            .WithBucket(_bucket)
            .WithObject(ToKey(path))).GetAwaiter().GetResult();
        return stat.Size;
    }

    /// <summary>最后写入时间 = 对象的 LastModified。</summary>
    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        try
        {
            var stat = _client.StatObjectAsync(new StatObjectArgs()
                .WithBucket(_bucket)
                .WithObject(ToKey(path))).GetAwaiter().GetResult();
            return stat.LastModified;
        }
        catch { return DateTime.MinValue; }
    }

    protected override DateTime GetCreationTimeImpl(UPath path) => GetLastWriteTimeImpl(path);

    protected override void SetLastWriteTimeImpl(UPath path, DateTime time) { }
    protected override void SetCreationTimeImpl(UPath path, DateTime time) { }

    /// <summary>
    /// 枚举路径：按前缀列出对象并映射回 VFS 路径，按搜索目标/通配符过滤。
    /// </summary>
    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchDepth searchOption, SearchTarget searchTarget)
    {
        // 目录路径需以 "/" 结尾才能作为前缀过滤出该目录下的子项。
        var prefix = ToKey(path);
        if (!prefix.EndsWith('/') && path != UPath.Root) prefix += "/";

        // 递归搜索时使用 recursive：true 一次取回整棵子树。
        var items = ListObjectsSync(prefix, recursive: searchOption == SearchDepth.AllDirectories);
        foreach (var item in items)
        {
            var itemPath = FromKey(item.Key);
            // 跳过"目录本身"（其对象键正是当前前缀）。
            if (itemPath == path) continue;

            // S3 目录是键以 "/" 结尾的占位对象，据此区分目录/文件。
            var isDir = item.Key.EndsWith('/');
            if (searchTarget == SearchTarget.File && isDir) continue;
            if (searchTarget == SearchTarget.Directory && !isDir) continue;

            if (!MatchesPattern(itemPath.GetName(), searchPattern)) continue;

            yield return itemPath;
        }
    }

    /// <summary>
    /// 打开文件：读模式从 S3 拉取到内存流；写模式使用释放时上传的 <see cref="S3WriteStream"/>。
    /// </summary>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var key = ToKey(path);

        if (access == FileAccess.Read || access == FileAccess.ReadWrite)
        {
            if (mode == FileMode.Create || mode == FileMode.CreateNew || mode == FileMode.Truncate)
                return new S3WriteStream(_client, _bucket, key);

            // 读取现有对象到内存流并定位到起点。
            var ms = new MemoryStream();
            _client.GetObjectAsync(new GetObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithCallbackStream(stream => stream.CopyTo(ms)))
                .GetAwaiter().GetResult();
            ms.Position = 0;
            return ms;
        }

        return new S3WriteStream(_client, _bucket, key);
    }

    /// <summary>S3 无真实目录，创建目录 = 写入一个键以 "/" 结尾的空占位对象。</summary>
    protected override void CreateDirectoryImpl(UPath path)
    {
        if (path == UPath.Root) return;
        var key = ToKey(path).TrimEnd('/') + "/";
        _client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(key)
            .WithStreamData(new MemoryStream())
            .WithObjectSize(0))
            .GetAwaiter().GetResult();
    }

    /// <summary>删除文件 = 删除对应对象。</summary>
    protected override void DeleteFileImpl(UPath path)
    {
        _client.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(_bucket)
            .WithObject(ToKey(path)))
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// 递归删除目录下所有子对象；非递归则只删除目录占位对象。
    /// </summary>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        var prefix = ToKey(path).TrimEnd('/') + "/";
        if (isRecursive)
        {
            // 先枚举前缀下的全部子对象，再逐个删除。
            foreach (var item in ListObjectsSync(prefix, recursive: true))
            {
                _client.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(item.Key))
                    .GetAwaiter().GetResult();
            }
        }
        else
        {
            _client.RemoveObjectAsync(new RemoveObjectArgs()
                .WithBucket(_bucket)
                .WithObject(prefix))
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>移动文件 = 服务器端复制对象后删除源对象。</summary>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        var srcKey = ToKey(srcPath);
        var destKey = ToKey(destPath);

        // 服务器端 Copy 后删除源，避免数据经本地中转。
        _client.CopyObjectAsync(new CopyObjectArgs()
            .WithBucket(_bucket)
            .WithObject(destKey)
            .WithCopyObjectSource(new CopySourceObjectArgs()
                .WithBucket(_bucket)
                .WithObject(srcKey)))
            .GetAwaiter().GetResult();

        _client.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(_bucket)
            .WithObject(srcKey))
            .GetAwaiter().GetResult();
    }

    /// <summary>移动目录：递归复制前缀下所有对象到目标前缀，再删除源对象。</summary>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        var srcPrefix = ToKey(srcPath).TrimEnd('/') + "/";
        var destPrefix = ToKey(destPath).TrimEnd('/') + "/";

        foreach (var item in ListObjectsSync(srcPrefix, recursive: true))
        {
            // 保持相对路径，逐对象复制前缀映射。
            var destKey = destPrefix + item.Key[srcPrefix.Length..];
            _client.CopyObjectAsync(new CopyObjectArgs()
                .WithBucket(_bucket)
                .WithObject(destKey)
                .WithCopyObjectSource(new CopySourceObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(item.Key)))
                .GetAwaiter().GetResult();

            _client.RemoveObjectAsync(new RemoveObjectArgs()
                .WithBucket(_bucket)
                .WithObject(item.Key))
                .GetAwaiter().GetResult();
        }
    }

    // --- S3 优化的复制（服务器端复制） ---

    /// <summary>复制文件 = 服务器端 CopyObject（避免经过本地中转整个对象）。</summary>
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!overwrite && FileExists(destPath))
            throw new IOException($"File already exists: {destPath}");

        _client.CopyObjectAsync(new CopyObjectArgs()
            .WithBucket(_bucket)
            .WithObject(ToKey(destPath))
            .WithCopyObjectSource(new CopySourceObjectArgs()
                .WithBucket(_bucket)
                .WithObject(ToKey(srcPath))))
            .GetAwaiter().GetResult();
    }

    // --- 辅助 ---

    /// <summary>同步方式枚举指定前缀下的对象列表（内部用异步枚举器阻塞等待）。</summary>
    private IEnumerable<Item> ListObjectsSync(string prefix, bool recursive)
    {
        var args = new ListObjectsArgs()
            .WithBucket(_bucket)
            .WithPrefix(prefix)
            .WithRecursive(recursive);

        // Minio 返回异步枚举器，这里同步遍历以适配 VFS 的同步 API。
        var asyncEnum = _client.ListObjectsEnumAsync(args);
        var enumerator = asyncEnum.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>仅写流：写入内容缓存在内存，Dispose 时一次性上传到 S3。</summary>
internal sealed class S3WriteStream : MemoryStream
{
    private readonly MinioClient _client;
    private readonly string _bucket;
    private readonly string _key;
    private bool _uploaded;

    public S3WriteStream(MinioClient client, string bucket, string key)
    {
        _client = client;
        _bucket = bucket;
        _key = key;
    }

    /// <summary>释放时把缓冲内容作为对象上传到 S3（仅执行一次）。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_uploaded)
        {
            _uploaded = true;
            Position = 0;
            // 将整段缓冲作为对象内容上传。
            _client.PutObjectAsync(new PutObjectArgs()
                .WithBucket(_bucket)
                .WithObject(_key)
                .WithStreamData(this)
                .WithObjectSize(Length))
                .GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}
