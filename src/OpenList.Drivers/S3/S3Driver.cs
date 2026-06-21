using Minio;
using Minio.DataModel.Args;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;

namespace OpenList.Drivers.S3;

public class S3Driver : ZioDriverBase, IFileSystemLinkProvider, IFileSystemUploader
{
    private IMinioClient? _client;
    private string _rootFolderPath = "/";

    public override DriverConfig Config { get; } = new()
    {
        Name = "S3",
        DefaultRoot = "/",
        LocalSort = true,
        CheckStatus = true,
        NoOverwriteUpload = true,
        LinkCacheMode = LinkCacheMode.IP | LinkCacheMode.UA
    };

    private S3Addition? _addition;
    public override object Addition => _addition ??= System.Text.Json.JsonSerializer.Deserialize<S3Addition>(_storage?.Addition ?? "{}") ?? new S3Addition();

    public override DriverInfo GetDriverInfo()
    {
        var additionalItems = BuildItemsFromObject(new S3Addition());
        var commonItems = new List<DriverItem>
        {
            new() { Name = "mount_path", Type = "string", Required = true, Help = "The path you want to mount to, it is unique and cannot be repeated" },
            new() { Name = "order", Type = "number", Help = "use to sort" },
            new() { Name = "remark", Type = "text" },
            new() { Name = "webdav_policy", Type = "select", Options = "use_proxy_url,native_proxy", Default = "native_proxy", Required = true },
            new() { Name = "down_proxy_url", Type = "text" },
            new() { Name = "disable_proxy_sign", Type = "bool", Default = "false" },
            new() { Name = "order_by", Type = "select", Options = "name,size,modified" },
            new() { Name = "order_direction", Type = "select", Options = "asc,desc" },
            new() { Name = "extract_folder", Type = "select", Options = "front,back" },
            new() { Name = "disable_index", Type = "bool", Default = "false", Required = true },
            new() { Name = "enable_sign", Type = "bool", Default = "false", Required = true },
        };
        return new DriverInfo { Common = commonItems, Additional = additionalItems, Config = Config };
    }

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (S3Addition)Addition;
        _rootFolderPath = addition.RootFolderPath;

        var builder = new MinioClient()
            .WithEndpoint(addition.Endpoint)
            .WithCredentials(addition.AccessKeyID, addition.SecretAccessKey);

        if (!string.IsNullOrEmpty(addition.SessionToken))
            builder = builder.WithSessionToken(addition.SessionToken);

        if (addition.ForcePathStyle)
            builder = builder.WithSSL(false);

        if (!string.IsNullOrEmpty(addition.Region))
            builder = builder.WithRegion(addition.Region);

        _client = builder.Build();
        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default)
    {
        _client = null;
        return Task.CompletedTask;
    }

    private S3Addition GetAdditionTyped() => (S3Addition)Addition;

    private static string GetKey(string path, bool dir)
    {
        var key = path.TrimStart('/');
        if (!string.IsNullOrEmpty(key) && dir)
            key += "/";
        return key;
    }

    private string GetFullKey(UPath path, bool dir)
    {
        var fullPath = string.Concat(_rootFolderPath.TrimEnd('/'), "/", path.FullName.TrimStart('/'));
        return GetKey(fullPath, dir);
    }

    private static string GetPlaceholderName(string? placeholder) =>
        string.IsNullOrEmpty(placeholder) ? ".openlist" : placeholder;

    private void EnsureConnected()
    {
        if (_client == null)
            InitAsync().GetAwaiter().GetResult();
    }

    #region IFileSystem Impl

    protected override void CreateDirectoryImpl(UPath path)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var placeholderKey = GetFullKey(path / GetPlaceholderName(addition.Placeholder), false);

        using var stream = new MemoryStream(Array.Empty<byte>());
        var putArgs = new PutObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(placeholderKey)
            .WithStreamData(stream)
            .WithObjectSize(0)
            .WithContentType("application/octet-stream");

        _client!.PutObjectAsync(putArgs).GetAwaiter().GetResult();
    }

    protected override bool DirectoryExistsImpl(UPath path)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var prefix = GetFullKey(path, true);

        var enumerator = _client!.ListObjectsEnumAsync(new ListObjectsArgs()
            .WithBucket(addition.Bucket)
            .WithPrefix(prefix)
            .WithRecursive(false))
            .GetAsyncEnumerator();

        try
        {
            return enumerator.MoveNextAsync().GetAwaiter().GetResult();
        }
        finally
        {
            enumerator.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        EnsureConnected();
        CopyDirRecursive(srcPath, destPath);
        DeleteDirectoryImpl(srcPath, isRecursive: true);
    }

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var prefix = GetFullKey(path, true);

        var objects = new List<string>();
        var enumerator = _client!.ListObjectsEnumAsync(new ListObjectsArgs()
            .WithBucket(addition.Bucket)
            .WithPrefix(prefix)
            .WithRecursive(true))
            .GetAsyncEnumerator();

        try
        {
            while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
            {
                objects.Add(enumerator.Current.Key);
            }
        }
        finally
        {
            enumerator.DisposeAsync().GetAwaiter().GetResult();
        }

        if (objects.Count > 0)
        {
            var removeArgs = new RemoveObjectsArgs()
                .WithBucket(addition.Bucket)
                .WithObjects(objects);
            _client.RemoveObjectsAsync(removeArgs).GetAwaiter().GetResult();
        }
    }

    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var srcKey = GetFullKey(srcPath, false);
        var dstKey = GetFullKey(destPath, false);

        var copySourceArgs = new CopySourceObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(srcKey);

        var copyDestArgs = new CopyObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(dstKey)
            .WithCopyObjectSource(copySourceArgs);

        _client!.CopyObjectAsync(copyDestArgs).GetAwaiter().GetResult();
    }

    protected override long GetFileLengthImpl(UPath path)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var key = GetFullKey(path, false);

        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(addition.Bucket)
                .WithObject(key);
            var stat = _client!.StatObjectAsync(statArgs).GetAwaiter().GetResult();
            return (long)stat.Size;
        }
        catch
        {
            return 0;
        }
    }

    protected override bool FileExistsImpl(UPath path)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var key = GetFullKey(path, false);

        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(addition.Bucket)
                .WithObject(key);
            _client!.StatObjectAsync(statArgs).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        CopyFileImpl(srcPath, destPath, overwrite: false);
        DeleteFileImpl(srcPath);
    }

    protected override void DeleteFileImpl(UPath path)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var key = GetFullKey(path, false);

        var removeArgs = new RemoveObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(key);
        _client!.RemoveObjectAsync(removeArgs).GetAwaiter().GetResult();
    }

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var key = GetFullKey(path, false);

        if (access.HasFlag(FileAccess.Read))
        {
            Stream? capturedStream = null;
            var getArgs = new GetObjectArgs()
                .WithBucket(addition.Bucket)
                .WithObject(key)
                .WithCallbackStream(stream =>
                {
                    var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    capturedStream = ms;
                });

            _client!.GetObjectAsync(getArgs).GetAwaiter().GetResult();
            return capturedStream ?? Stream.Null;
        }

        // For write: return a stream that uploads on dispose
        return new S3WriteStream(_client!, addition.Bucket, key);
    }

    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var key = GetFullKey(path, false);

        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(addition.Bucket)
                .WithObject(key);
            var stat = _client!.StatObjectAsync(statArgs).GetAwaiter().GetResult();
            return stat.LastModified.ToUniversalTime();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var prefix = GetFullKey(path, true);

        var enumerator = _client!.ListObjectsEnumAsync(new ListObjectsArgs()
            .WithBucket(addition.Bucket)
            .WithPrefix(prefix)
            .WithRecursive(false))
            .GetAsyncEnumerator();

        try
        {
            while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
            {
                var obj = enumerator.Current;
                var name = obj.IsDir
                    ? Path.GetFileName(obj.Key.TrimEnd('/'))
                    : Path.GetFileName(obj.Key);

                if (string.IsNullOrEmpty(name)) continue;

                // Skip placeholder unless showing it
                var placeholder = GetPlaceholderName(addition.Placeholder);
                if (name == placeholder || name == addition.Placeholder) continue;

                var itemPath = path / name;
                var item = new FileSystemItem(this, itemPath, obj.IsDir)
                {
                    Length = (long)obj.Size,
                    LastWriteTime = DateTime.TryParse(obj.LastModified, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow
                };

                if (searchPredicate == null || searchPredicate(ref item))
                    yield return item;
            }
        }
        finally
        {
            enumerator.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        foreach (var item in EnumerateItemsImpl(path, searchOption, null))
        {
            if (searchTarget == SearchTarget.File && item.IsDirectory) continue;
            if (searchTarget == SearchTarget.Directory && !item.IsDirectory) continue;
            yield return item.Path;
        }
    }

    #endregion

    #region IFileSystemLinkProvider

    public async Task<Link?> GetLinkAsync(UPath path, LinkArgs args, CancellationToken ct = default)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var key = GetFullKey(path, false);
        var expireHours = int.TryParse(addition.SignURLExpire, out var h) ? h : 4;

        var presignedArgs = new PresignedGetObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(key)
            .WithExpiry(expireHours * 3600);

        var url = await _client!.PresignedGetObjectAsync(presignedArgs);

        var link = new Link { URL = url };

        if (!string.IsNullOrEmpty(addition.CustomHost))
        {
            link.URL = ReplaceHost(link.URL, addition.CustomHost);
        }

        return link;
    }

    private static string ReplaceHost(string url, string customHost)
    {
        try
        {
            var uri = new Uri(url);
            var split = customHost.Split("://", 2);
            var builder = new UriBuilder(uri);
            if (split.Length == 2 && (split[0] == "http" || split[0] == "https"))
            {
                builder.Scheme = split[0];
                builder.Host = split[1];
            }
            else
            {
                builder.Host = customHost;
            }
            return builder.Uri.ToString();
        }
        catch
        {
            return url;
        }
    }

    #endregion

    #region IFileSystemUploader

    public async Task<UPath> UploadAsync(UPath dstDir, Stream stream, string fileName, Action<double> progress, CancellationToken ct = default)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var filePath = dstDir / fileName;
        var key = GetFullKey(filePath, false);
        var contentType = "application/octet-stream";

        var size = stream.CanSeek ? stream.Length : -1;

        var putArgs = new PutObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(key)
            .WithStreamData(stream)
            .WithObjectSize(size)
            .WithContentType(contentType);

        await _client!.PutObjectAsync(putArgs, ct);
        progress(1.0);
        return filePath;
    }

    #endregion

    #region Helpers

    private void CopyDirRecursive(UPath srcPath, UPath destPath)
    {
        EnsureConnected();
        var addition = GetAdditionTyped();
        var prefix = GetFullKey(srcPath, true);

        var enumerator = _client!.ListObjectsEnumAsync(new ListObjectsArgs()
            .WithBucket(addition.Bucket)
            .WithPrefix(prefix)
            .WithRecursive(true))
            .GetAsyncEnumerator();

        try
        {
            while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
            {
                var obj = enumerator.Current;
                var relativePath = obj.Key[prefix.Length..];
                var dstKey = GetFullKey(destPath / relativePath, false);

                var copySourceArgs = new CopySourceObjectArgs()
                    .WithBucket(addition.Bucket)
                    .WithObject(obj.Key);

                var copyDestArgs = new CopyObjectArgs()
                    .WithBucket(addition.Bucket)
                    .WithObject(dstKey)
                    .WithCopyObjectSource(copySourceArgs);

                _client.CopyObjectAsync(copyDestArgs).GetAwaiter().GetResult();
            }
        }
        finally
        {
            enumerator.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    #endregion
}

/// <summary>
/// A stream that uploads to S3 on dispose.
/// </summary>
internal class S3WriteStream : MemoryStream
{
    private readonly IMinioClient _client;
    private readonly string _bucket;
    private readonly string _key;
    private bool _uploaded;

    public S3WriteStream(IMinioClient client, string bucket, string key)
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
            var putArgs = new PutObjectArgs()
                .WithBucket(_bucket)
                .WithObject(_key)
                .WithStreamData(this)
                .WithObjectSize(Length)
                .WithContentType("application/octet-stream");
            _client.PutObjectAsync(putArgs).GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}

public class S3Addition
{
    public string RootFolderPath { get; set; } = "/";
    [System.ComponentModel.Description("Bucket name")]
    [System.ComponentModel.DataAnnotations.Required]
    public string Bucket { get; set; } = "";
    [System.ComponentModel.Description("S3 endpoint URL")]
    [System.ComponentModel.DataAnnotations.Required]
    public string Endpoint { get; set; } = "";
    [System.ComponentModel.Description("Region")]
    public string Region { get; set; } = "";
    [System.ComponentModel.Description("Access Key ID")]
    [System.ComponentModel.DataAnnotations.Required]
    public string AccessKeyID { get; set; } = "";
    [System.ComponentModel.Description("Secret Access Key")]
    [System.ComponentModel.DataAnnotations.Required]
    public string SecretAccessKey { get; set; } = "";
    [System.ComponentModel.Description("Session Token")]
    public string? SessionToken { get; set; }
    [System.ComponentModel.Description("Force path-style access")]
    public bool ForcePathStyle { get; set; }
    [System.ComponentModel.Description("Custom host for presigned URLs")]
    public string? CustomHost { get; set; }
    [System.ComponentModel.Description("Use custom host for presigned URLs")]
    public bool EnableCustomHostPresign { get; set; }
    [System.ComponentModel.Description("Presigned URL expiration (hours)")]
    public string SignURLExpire { get; set; } = "4";
    [System.ComponentModel.Description("Placeholder file pattern")]
    public string? Placeholder { get; set; }
    [System.ComponentModel.Description("List objects API version")]
    [DriverItemType("select")]
    [DriverItemOptions("v1,v2")]
    public string ListObjectVersion { get; set; } = "v1";
    [System.ComponentModel.Description("Remove bucket from path")]
    public bool RemoveBucket { get; set; }
    [System.ComponentModel.Description("Add filename to Content-Disposition header")]
    public bool AddFilenameToDisposition { get; set; }
    [System.ComponentModel.Description("Enable direct upload")]
    public bool EnableDirectUpload { get; set; }
    [System.ComponentModel.Description("Direct upload host")]
    public string? DirectUploadHost { get; set; }
}
