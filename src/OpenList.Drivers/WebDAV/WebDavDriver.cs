using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.WebDAV;

public class WebDavDriver : RootPathDriver, IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename, IDriverCopy, IDriverRemove, IDriverPutResult
{
    public override DriverConfig Config() => new()
    {
        Name = "WebDAV",
        DefaultRoot = "/",
        CheckStatus = true
    };

    public override object GetAddition() => System.Text.Json.JsonSerializer.Deserialize<WebDavAddition>(_storage.Addition) ?? new WebDavAddition();

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (WebDavAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;
        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;

    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        // WebDAV PROPFIND implementation would go here
        return Task.FromResult(new List<IObj>());
    }

    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        // WebDAV GET link generation
        return Task.FromResult<Link?>(null);
    }

    public Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        // WebDAV PROPFIND for single resource
        return Task.FromResult<IObj?>(null);
    }

    public Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        // WebDAV MKCOL
        return Task.FromResult<IObj>(new ObjItem { Name = dirName, IsFolder = true, Path = parentDir.GetPath() + "/" + dirName });
    }

    public Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        // WebDAV MOVE
        return Task.CompletedTask;
    }

    public Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        // WebDAV MOVE (rename)
        return Task.CompletedTask;
    }

    public Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        // WebDAV COPY
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        // WebDAV DELETE
        return Task.CompletedTask;
    }

    public Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        // WebDAV PUT
        return Task.FromResult<IObj>(new ObjItem { Name = file.Obj.GetName(), IsFolder = false, Path = dstDir.GetPath() + "/" + file.Obj.GetName() });
    }
}

public class WebDavAddition
{
    public string RootFolderPath { get; set; } = "/";
    public string WebdavURL { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ProxyAddress { get; set; }
    public string? Authorization { get; set; }
}
