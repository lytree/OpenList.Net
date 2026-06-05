using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Local;

public class LocalDriver : RootPathDriver, IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename, IDriverCopy, IDriverRemove, IDriverPutResult
{
    public override DriverConfig Config() => new()
    {
        Name = "Local",
        LocalSort = true,
        DefaultRoot = "/",
        CheckStatus = true
    };

    public override object GetAddition() => JsonSerializer.Deserialize<LocalAddition>(_storage.Addition) ?? new LocalAddition();

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;
        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;

    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        var path = GetActualPath(dir.GetPath());
        if (!Directory.Exists(path)) return Task.FromResult(new List<IObj>());

        var result = new List<IObj>();
        var dirInfo = new DirectoryInfo(path);

        try
        {
            foreach (var d in dirInfo.EnumerateDirectories())
            {
                result.Add(new ObjItem
                {
                    Name = d.Name,
                    Size = 0,
                    Modified = d.LastWriteTimeUtc,
                    CTime = d.CreationTimeUtc,
                    IsFolder = true,
                    Path = dir.GetPath() + "/" + d.Name
                });
            }
        }
        catch (UnauthorizedAccessException) { }

        try
        {
            foreach (var f in dirInfo.EnumerateFiles())
            {
                result.Add(new ObjItem
                {
                    Name = f.Name,
                    Size = f.Length,
                    Modified = f.LastWriteTimeUtc,
                    CTime = f.CreationTimeUtc,
                    IsFolder = false,
                    Path = dir.GetPath() + "/" + f.Name
                });
            }
        }
        catch (UnauthorizedAccessException) { }

        return Task.FromResult(result);
    }

    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        var path = GetActualPath(file.GetPath());
        if (!File.Exists(path)) return Task.FromResult<Link?>(null);
        return Task.FromResult<Link?>(new Link { URL = path });
    }

    public Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        var actualPath = GetActualPath(path);
        if (File.Exists(actualPath))
        {
            var fi = new FileInfo(actualPath);
            return Task.FromResult<IObj?>(new ObjItem
            {
                Name = fi.Name, Size = fi.Length, Modified = fi.LastWriteTimeUtc,
                CTime = fi.CreationTimeUtc, IsFolder = false, Path = path
            });
        }
        if (Directory.Exists(actualPath))
        {
            var di = new DirectoryInfo(actualPath);
            return Task.FromResult<IObj?>(new ObjItem
            {
                Name = di.Name, Size = 0, Modified = di.LastWriteTimeUtc,
                CTime = di.CreationTimeUtc, IsFolder = true, Path = path
            });
        }
        return Task.FromResult<IObj?>(null);
    }

    public Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        var path = Path.Join(GetActualPath(parentDir.GetPath()), dirName);
        Directory.CreateDirectory(path);
        var di = new DirectoryInfo(path);
        return Task.FromResult<IObj>(new ObjItem
        {
            Name = di.Name, IsFolder = true, Modified = di.LastWriteTimeUtc,
            CTime = di.CreationTimeUtc, Path = parentDir.GetPath() + "/" + dirName
        });
    }

    public Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var srcPath = GetActualPath(srcObj.GetPath());
        var dstPath = Path.Join(GetActualPath(dstDir.GetPath()), srcObj.GetName());
        if (srcObj.IsDir()) Directory.Move(srcPath, dstPath);
        else File.Move(srcPath, dstPath);
        return Task.CompletedTask;
    }

    public Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        var srcPath = GetActualPath(srcObj.GetPath());
        var dir = Path.GetDirectoryName(srcPath)!;
        var dstPath = Path.Join(dir, newName);
        if (srcObj.IsDir()) Directory.Move(srcPath, dstPath);
        else File.Move(srcPath, dstPath);
        return Task.CompletedTask;
    }

    public Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var srcPath = GetActualPath(srcObj.GetPath());
        var dstPath = Path.Join(GetActualPath(dstDir.GetPath()), srcObj.GetName());
        if (srcObj.IsDir()) CopyDirectory(srcPath, dstPath);
        else File.Copy(srcPath, dstPath, true);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        var path = GetActualPath(obj.GetPath());
        if (obj.IsDir()) Directory.Delete(path, true);
        else File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        var dirPath = GetActualPath(dstDir.GetPath());
        var filePath = Path.Join(dirPath, file.Obj.GetName());
        Directory.CreateDirectory(dirPath);
        using var fs = new FileStream(filePath, FileMode.Create);
        if (file.Stream != null)
        {
            await file.Stream.CopyToAsync(fs, ct);
        }
        var fi = new FileInfo(filePath);
        return new ObjItem
        {
            Name = fi.Name, Size = fi.Length, Modified = fi.LastWriteTimeUtc,
            CTime = fi.CreationTimeUtc, IsFolder = false, Path = dstDir.GetPath() + "/" + file.Obj.GetName()
        };
    }

    private string GetActualPath(string virtualPath)
    {
        var root = RootFolderPath;
        if (string.IsNullOrEmpty(root)) root = "/";
        var relative = virtualPath.TrimStart('/');
        return string.IsNullOrEmpty(relative) ? root : Path.Join(root, relative);
    }

    private static void CopyDirectory(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var file in Directory.GetFiles(srcDir))
            File.Copy(file, Path.Join(dstDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(srcDir))
            CopyDirectory(dir, Path.Join(dstDir, Path.GetFileName(dir)));
    }
}

public class LocalAddition
{
    public string RootFolderPath { get; set; } = "/";
    public bool ShowHidden { get; set; } = true;
    public bool Recursion { get; set; }
    public string? Thumbnail { get; set; }
    public string? ThumbCacheFolder { get; set; }
}
