using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Ftp;

public class FtpDriver : RootPathDriver, IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename, IDriverCopy, IDriverRemove, IDriverPutResult
{
    public override DriverConfig Config() => new() { Name = "FTP", DefaultRoot = "/", CheckStatus = true };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
    public Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default) => Task.FromResult<IObj>(new ObjItem { Name = dirName, IsFolder = true });
    public Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default) => Task.CompletedTask;
    public Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(IObj obj, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default) => Task.FromResult<IObj>(new ObjItem { Name = file.Obj.GetName(), IsFolder = false });
}
