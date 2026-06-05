using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.S3;

public class S3Driver : RootPathDriver, IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename, IDriverCopy, IDriverRemove, IDriverPutResult
{
    public override DriverConfig Config() => new()
    {
        Name = "S3",
        DefaultRoot = "/",
        CheckStatus = true,
        NoOverwriteUpload = true,
        LinkCacheMode = LinkCacheMode.IP | LinkCacheMode.UA
    };

    public override object GetAddition() => System.Text.Json.JsonSerializer.Deserialize<S3Addition>(_storage.Addition) ?? new S3Addition();

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (S3Addition)GetAddition();
        RootFolderPath = addition.RootFolderPath;
        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;

    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        // S3 ListObjectsV2 implementation
        return Task.FromResult(new List<IObj>());
    }

    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        // S3 presigned URL generation
        return Task.FromResult<Link?>(null);
    }

    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
    public Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default) => Task.FromResult<IObj>(new ObjItem { Name = dirName, IsFolder = true });
    public Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default) => Task.CompletedTask;
    public Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(IObj obj, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default) => Task.FromResult<IObj>(new ObjItem { Name = file.Obj.GetName(), IsFolder = false });
}

public class S3Addition
{
    public string RootFolderPath { get; set; } = "/";
    public string Bucket { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "";
    public string AccessKeyID { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string? SessionToken { get; set; }
    public bool ForcePathStyle { get; set; }
    public string? CustomHost { get; set; }
    public string? SignURLExpire { get; set; } = "4";
}
