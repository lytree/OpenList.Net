using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.S3;

public class S3Driver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverCopy, IDriverRemove, IDriverPutResult
{
    private IMinioClient? _client;

    public override DriverConfig Config() => new()
    {
        Name = "S3",
        DefaultRoot = "/",
        LocalSort = true,
        CheckStatus = true,
        NoOverwriteUpload = true,
        LinkCacheMode = LinkCacheMode.IP | LinkCacheMode.UA
    };

    public override object GetAddition() => System.Text.Json.JsonSerializer.Deserialize<S3Addition>(_storage.Addition) ?? new S3Addition();
    protected override Type? GetAdditionType() => typeof(S3Addition);

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (S3Addition)GetAddition();
        RootFolderPath = addition.RootFolderPath;

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

    private S3Addition GetAdditionTyped() => (S3Addition)GetAddition();

    private static string GetKey(string path, bool dir)
    {
        var key = path.TrimStart('/');
        if (!string.IsNullOrEmpty(key) && dir)
            key += "/";
        return key;
    }

    #region List

    public override async Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        var addition = GetAdditionTyped();
        var dirPath = dir.GetPath();
        var prefix = GetKey(string.Concat(RootFolderPath.TrimEnd('/'), "/", dirPath.TrimStart('/')), true);
        var files = new List<IObj>();

        await foreach (var obj in _client!.ListObjectsEnumAsync(new ListObjectsArgs()
            .WithBucket(addition.Bucket)
            .WithPrefix(prefix)
            .WithRecursive(false)))
        {
            if (obj.IsDir)
            {
                var name = Path.GetFileName(obj.Key.TrimEnd('/'));
                if (!string.IsNullOrEmpty(name))
                {
                    files.Add(new ObjItem
                    {
                        Name = name,
                        IsFolder = true,
                        Path = string.Concat(dirPath.TrimEnd('/'), "/", name),
                        Modified = DateTime.TryParse(obj.LastModified, out var dt1) ? dt1.ToUniversalTime() : DateTime.UtcNow
                    });
                }
            }
            else
            {
                var name = Path.GetFileName(obj.Key);
                if (string.IsNullOrEmpty(name)) continue;
                if (!ShouldShowPlaceholder(addition, name, args)) continue;
                files.Add(new ObjItem
                {
                    Name = name,
                    IsFolder = false,
                    Size = (long)obj.Size,
                    Path = string.Concat(dirPath.TrimEnd('/'), "/", name),
                    Modified = DateTime.TryParse(obj.LastModified, out var dt2) ? dt2.ToUniversalTime() : DateTime.UtcNow
                });
            }
        }

        return files;
    }

    private static bool ShouldShowPlaceholder(S3Addition addition, string name, ListArgs args)
    {
        var placeholder = GetPlaceholderName(addition.Placeholder);
        if (name == placeholder || name == addition.Placeholder)
            return args.S3ShowPlaceholder;
        return true;
    }

    private static string GetPlaceholderName(string? placeholder) =>
        string.IsNullOrEmpty(placeholder) ? ".openlist" : placeholder;

    #endregion

    #region Link

    public override async Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        var addition = GetAdditionTyped();
        var filePath = string.Concat(RootFolderPath.TrimEnd('/'), "/", file.GetPath().TrimStart('/'));
        var key = GetKey(filePath, false);
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

    #region Get

    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        var addition = GetAdditionTyped();
        var fullPath = string.Concat(RootFolderPath.TrimEnd('/'), "/", path.TrimStart('/'));
        var key = GetKey(fullPath, false);

        // Try HeadObject first (file)
        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(addition.Bucket)
                .WithObject(key);

            var stat = await _client!.StatObjectAsync(statArgs, ct);
            var name = Path.GetFileName(fullPath);
            return new ObjItem
            {
                Name = name,
                IsFolder = false,
                Size = (long)stat.Size,
                Modified = stat.LastModified.ToUniversalTime(),
                Path = fullPath
            };
        }
        catch
        {
            // Not a file, check if directory
        }

        // Check if it's a directory by listing with prefix
        var prefix = GetKey(fullPath, true);
        await foreach (var obj in _client!.ListObjectsEnumAsync(new ListObjectsArgs()
            .WithBucket(addition.Bucket)
            .WithPrefix(prefix)
            .WithRecursive(false)))
        {
            // At least one item exists, so it's a directory
            var name = Path.GetFileName(fullPath);
            return new ObjItem
            {
                Name = name,
                IsFolder = true,
                Path = fullPath,
                Modified = DateTime.UtcNow
            };
        }

        return null;
    }

    #endregion

    #region MakeDir

    public async Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        var addition = GetAdditionTyped();
        var dirPath = string.Concat(parentDir.GetPath().TrimEnd('/'), "/", dirName);
        var fullPath = string.Concat(RootFolderPath.TrimEnd('/'), "/", dirPath.TrimStart('/'));
        var placeholderKey = GetKey(string.Concat(fullPath.TrimEnd('/'), "/", GetPlaceholderName(addition.Placeholder)), false);

        // S3 directories are virtual — create a placeholder object
        using var stream = new MemoryStream(Array.Empty<byte>());
        var putArgs = new PutObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(placeholderKey)
            .WithStreamData(stream)
            .WithObjectSize(0)
            .WithContentType("application/octet-stream");

        await _client!.PutObjectAsync(putArgs, ct);

        return new ObjItem
        {
            Name = dirName,
            IsFolder = true,
            Path = dirPath,
            Modified = DateTime.UtcNow
        };
    }

    #endregion

    #region Move

    public async Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        await CopyAsync(srcObj, dstDir, ct);
        await RemoveAsync(srcObj, ct);
    }

    #endregion

    #region Rename

    public async Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        var parentPath = srcObj.GetPath().TrimEnd('/');
        parentPath = parentPath[..parentPath.LastIndexOf('/')];
        var dstPath = string.Concat(parentPath, "/", newName);

        if (srcObj.IsDir())
            await CopyDirAsync(srcObj.GetPath(), dstPath, ct);
        else
            await CopyFileAsync(srcObj.GetPath(), dstPath, ct);

        await RemoveAsync(srcObj, ct);
    }

    #endregion

    #region Copy

    public async Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var dstPath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", srcObj.GetName());
        if (srcObj.IsDir())
            await CopyDirAsync(srcObj.GetPath(), dstPath, ct);
        else
            await CopyFileAsync(srcObj.GetPath(), dstPath, ct);
    }

    private async Task CopyFileAsync(string srcPath, string dstPath, CancellationToken ct)
    {
        var addition = GetAdditionTyped();
        var srcKey = GetKey(string.Concat(RootFolderPath.TrimEnd('/'), "/", srcPath.TrimStart('/')), false);
        var dstKey = GetKey(string.Concat(RootFolderPath.TrimEnd('/'), "/", dstPath.TrimStart('/')), false);

        var copySourceArgs = new CopySourceObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(srcKey);

        var copyDestArgs = new CopyObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(dstKey)
            .WithCopyObjectSource(copySourceArgs);

        await _client!.CopyObjectAsync(copyDestArgs, ct);
    }

    private async Task CopyDirAsync(string srcPath, string dstPath, CancellationToken ct)
    {
        var addition = GetAdditionTyped();
        var prefix = GetKey(string.Concat(RootFolderPath.TrimEnd('/'), "/", srcPath.TrimStart('/')), true);

        await foreach (var obj in _client!.ListObjectsEnumAsync(new ListObjectsArgs()
            .WithBucket(addition.Bucket)
            .WithPrefix(prefix)
            .WithRecursive(true)))
        {
            var relativePath = obj.Key[prefix.Length..];
            var newDstPath = string.Concat(dstPath, "/", relativePath);
            var dstKey = GetKey(string.Concat(RootFolderPath.TrimEnd('/'), "/", newDstPath.TrimStart('/')), false);

            var copySourceArgs = new CopySourceObjectArgs()
                .WithBucket(addition.Bucket)
                .WithObject(obj.Key);

            var copyDestArgs = new CopyObjectArgs()
                .WithBucket(addition.Bucket)
                .WithObject(dstKey)
                .WithCopyObjectSource(copySourceArgs);

            await _client.CopyObjectAsync(copyDestArgs, ct);
        }
    }

    #endregion

    #region Remove

    public async Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        if (obj.IsDir())
            await RemoveDirAsync(obj.GetPath(), ct);
        else
            await RemoveFileAsync(obj.GetPath(), ct);
    }

    private async Task RemoveFileAsync(string path, CancellationToken ct)
    {
        var addition = GetAdditionTyped();
        var key = GetKey(string.Concat(RootFolderPath.TrimEnd('/'), "/", path.TrimStart('/')), false);

        var removeArgs = new RemoveObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(key);

        await _client!.RemoveObjectAsync(removeArgs, ct);
    }

    private async Task RemoveDirAsync(string srcPath, CancellationToken ct)
    {
        var addition = GetAdditionTyped();
        var prefix = GetKey(string.Concat(RootFolderPath.TrimEnd('/'), "/", srcPath.TrimStart('/')), true);

        var objects = new List<string>();
        await foreach (var obj in _client!.ListObjectsEnumAsync(new ListObjectsArgs()
            .WithBucket(addition.Bucket)
            .WithPrefix(prefix)
            .WithRecursive(true)))
        {
            objects.Add(obj.Key);
        }

        if (objects.Count > 0)
        {
            var removeArgs = new RemoveObjectsArgs()
                .WithBucket(addition.Bucket)
                .WithObjects(objects);

            await _client.RemoveObjectsAsync(removeArgs, ct);
        }
    }

    #endregion

    #region Put

    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        var addition = GetAdditionTyped();
        var remotePath = string.Concat(dstDir.GetPath().TrimEnd('/'), "/", file.Obj.GetName());
        var key = GetKey(string.Concat(RootFolderPath.TrimEnd('/'), "/", remotePath.TrimStart('/')), false);
        var contentType = file.Mimetype ?? "application/octet-stream";

        var stream = file.Stream ?? Stream.Null;
        var size = file.Size > 0 ? file.Size : stream.Length;

        var putArgs = new PutObjectArgs()
            .WithBucket(addition.Bucket)
            .WithObject(key)
            .WithStreamData(stream)
            .WithObjectSize(size)
            .WithContentType(contentType);

        await _client!.PutObjectAsync(putArgs, ct);

        return new ObjItem
        {
            Name = file.Obj.GetName(),
            IsFolder = false,
            Size = file.Size,
            Path = remotePath,
            Modified = DateTime.UtcNow
        };
    }

    #endregion
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
