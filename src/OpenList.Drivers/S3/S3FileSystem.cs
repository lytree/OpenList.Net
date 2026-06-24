using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Zio;

namespace OpenList.Drivers.S3;

/// <summary>
/// Zio <see cref="FileSystem"/> backed by an S3-compatible bucket (Minio, AWS S3, etc.).
/// S3 is object storage: "directories" are key prefixes, "files" are objects.
/// </summary>
internal sealed class S3FileSystem : NetworkFileSystemBase
{
    private readonly MinioClient _client;
    private readonly string _bucket;
    private readonly string _rootPrefix;

    public S3FileSystem(MinioClient client, string bucket, string rootPrefix = "")
    {
        _client = client;
        _bucket = bucket;
        _rootPrefix = string.IsNullOrEmpty(rootPrefix) ? "" :
            (rootPrefix.EndsWith('/') ? rootPrefix : rootPrefix + "/");
    }

    // --- Path helpers ---

    private string ToKey(UPath path)
    {
        var relative = path.FullName.TrimStart('/');
        return string.IsNullOrEmpty(relative) ? _rootPrefix : _rootPrefix + relative;
    }

    private UPath FromKey(string key)
    {
        var relative = key.StartsWith(_rootPrefix, StringComparison.Ordinal)
            ? key[_rootPrefix.Length..]
            : key;
        return UPath.Root / relative;
    }

    // --- Core overrides ---

    protected override bool DirectoryExistsImpl(UPath path)
    {
        if (path == UPath.Root) return true;
        var prefix = ToKey(path).TrimEnd('/') + "/";
        return ListObjectsSync(prefix, recursive: false).Any();
    }

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

    protected override long GetFileLengthImpl(UPath path)
    {
        var stat = _client.StatObjectAsync(new StatObjectArgs()
            .WithBucket(_bucket)
            .WithObject(ToKey(path))).GetAwaiter().GetResult();
        return stat.Size;
    }

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

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        var prefix = ToKey(path);
        if (!prefix.EndsWith('/') && path != UPath.Root) prefix += "/";

        var items = ListObjectsSync(prefix, recursive: searchOption == SearchOption.AllDirectories);
        foreach (var item in items)
        {
            var itemPath = FromKey(item.Key);
            if (itemPath == path) continue;

            var isDir = item.Key.EndsWith('/');
            if (searchTarget == SearchTarget.File && isDir) continue;
            if (searchTarget == SearchTarget.Directory && !isDir) continue;

            if (!MatchesPattern(itemPath.GetName(), searchPattern)) continue;

            yield return itemPath;
        }
    }

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var key = ToKey(path);

        if (access == FileAccess.Read || access == FileAccess.ReadWrite)
        {
            if (mode == FileMode.Create || mode == FileMode.CreateNew || mode == FileMode.Truncate)
                return new S3WriteStream(_client, _bucket, key);

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

    protected override void DeleteFileImpl(UPath path)
    {
        _client.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(_bucket)
            .WithObject(ToKey(path)))
            .GetAwaiter().GetResult();
    }

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        var prefix = ToKey(path).TrimEnd('/') + "/";
        if (isRecursive)
        {
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

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        var srcKey = ToKey(srcPath);
        var destKey = ToKey(destPath);

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

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        var srcPrefix = ToKey(srcPath).TrimEnd('/') + "/";
        var destPrefix = ToKey(destPath).TrimEnd('/') + "/";

        foreach (var item in ListObjectsSync(srcPrefix, recursive: true))
        {
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

    // --- S3-optimized CopyFile (server-side copy) ---

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

    // --- Helpers ---

    private IEnumerable<Item> ListObjectsSync(string prefix, bool recursive)
    {
        var args = new ListObjectsArgs()
            .WithBucket(_bucket)
            .WithPrefix(prefix)
            .WithRecursive(recursive);

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

/// <summary>Write-only stream that uploads to S3 on dispose.</summary>
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_uploaded)
        {
            _uploaded = true;
            Position = 0;
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
