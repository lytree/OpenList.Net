using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using Zio;
using DirectUploadInfo = OpenList.Core.Interfaces.DirectUploadInfo;
using ArchiveEntry = OpenList.Core.Interfaces.ArchiveEntry;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// File system operations routed through <see cref="ZioMountService"/>'s <see cref="MountFileSystem"/>.
/// Mirrors OpenList Go <c>internal/fs</c>. Operations are path-based; the mount service resolves
/// the underlying driver. Optional driver capabilities (link/upload/archive) are probed via
/// the extension interfaces in <see cref="OpenList.Core.Drivers"/>.
/// </summary>
public sealed class FileSystemService : IFileSystemService
{
    private readonly ZioMountService _mount;

    public FileSystemService(ZioMountService mount) => _mount = mount;

    public Task<Resp<FsListResp>> ListAsync(FsListReq req, User user, CancellationToken ct = default)
    {
        var path = Normalize(req.Path);
        if (!_mount.Root.DirectoryExists(path))
            return Task.FromResult(Resp<FsListResp>.Fail("path not found", RespCodes.NotFound));

        var entries = _mount.Root.EnumeratePaths(path)
            .Select(p => _mount.Root.FileExists(p)
                ? (FileSystemEntry)_mount.Root.GetFileEntry(p)
                : _mount.Root.GetDirectoryEntry(p))
            .Select(ToObj)
            .OrderBy(e => !e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = entries.Count;
        var page = Math.Max(1, req.Page);
        var perPage = Math.Max(1, req.PerPage);
        var paged = entries.Skip((page - 1) * perPage).Take(perPage).ToList();

        var driver = _mount.FindDriverForPath(req.Path);
        return Task.FromResult(Resp<FsListResp>.Ok(new FsListResp
        {
            Content = paged,
            Total = total,
            Write = driver?.ReadOnly == false,
            Provider = driver?.DriverName,
        }));
    }

    public Task<Resp<FsGetResp>> GetAsync(FsGetReq req, User user, CancellationToken ct = default)
    {
        var path = Normalize(req.Path);
        var isFile = _mount.Root.FileExists(path);
        var isDir = _mount.Root.DirectoryExists(path);
        if (!isFile && !isDir)
            return Task.FromResult(Resp<FsGetResp>.Fail("path not found", RespCodes.NotFound));

        FileSystemEntry? entry = isFile
            ? _mount.Root.GetFileEntry(path)
            : isDir ? _mount.Root.GetDirectoryEntry(path) : null;
        var driver = _mount.FindDriverForPath(req.Path);
        var resp = new FsGetResp
        {
            Provider = driver?.DriverName,
        };
        if (entry is not null)
        {
            var obj = ToObj(entry);
            resp.Name = obj.Name;
            resp.Size = obj.Size;
            resp.IsDir = obj.IsDir;
            resp.Modified = obj.Modified;
            resp.Created = obj.Created;
            resp.Type = obj.Type;
        }
        return Task.FromResult(Resp<FsGetResp>.Ok(resp));
    }

    public Task<Resp> MkdirAsync(string path, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        if (_mount.Root.FileExists(upath) || _mount.Root.DirectoryExists(upath))
            return Task.FromResult(Resp.Fail("path already exists", RespCodes.Conflict));
        try
        {
            _mount.Root.CreateDirectory(upath);
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    public Task<Resp> RenameAsync(string oldPath, string newPath, User user, CancellationToken ct = default)
    {
        var src = Normalize(oldPath);
        var dst = Normalize(newPath);
        if (!_mount.Root.FileExists(src) && !_mount.Root.DirectoryExists(src))
            return Task.FromResult(Resp.Fail("source not found", RespCodes.NotFound));
        try
        {
            if (_mount.Root.FileExists(src))
                _mount.Root.MoveFile(src, dst);
            else
                _mount.Root.MoveDirectory(src, dst);
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    public Task<Resp> BatchRenameAsync(IReadOnlyList<BatchRenameItem> items, User user, CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            var r = RenameAsync(item.SrcName, item.NewName, user, ct).GetAwaiter().GetResult();
            if (r.Code != 200) return Task.FromResult(r);
        }
        return Task.FromResult(Resp.Ok());
    }

    public Task<Resp> RegexRenameAsync(string srcDir, string srcReg, string newReg, User user, CancellationToken ct = default)
        => Task.FromResult(Resp.Fail("regex rename not implemented", RespCodes.NotImplemented));

    public Task<Resp> MoveAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default)
    {
        var dst = Normalize(destDir);
        if (!_mount.Root.DirectoryExists(dst))
            return Task.FromResult(Resp.Fail("destination not found", RespCodes.NotFound));
        try
        {
            foreach (var src in srcPaths)
            {
                var s = Normalize(src);
                if (!_mount.Root.FileExists(s) && !_mount.Root.DirectoryExists(s)) continue;
                var target = UPath.Combine(dst, s.GetName());
                if (_mount.Root.FileExists(s))
                    _mount.Root.MoveFile(s, target);
                else
                    _mount.Root.MoveDirectory(s, target);
            }
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    public Task<Resp> RecursiveMoveAsync(string srcPath, string destDir, User user, CancellationToken ct = default)
        => MoveAsync([srcPath], destDir, user, ct);

    public Task<Resp> CopyAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default)
    {
        var dst = Normalize(destDir);
        if (!_mount.Root.DirectoryExists(dst))
            return Task.FromResult(Resp.Fail("destination not found", RespCodes.NotFound));
        try
        {
            foreach (var src in srcPaths)
            {
                var s = Normalize(src);
                if (!_mount.Root.FileExists(s) && !_mount.Root.DirectoryExists(s)) continue;
                var target = UPath.Combine(dst, s.GetName());
                if (_mount.Root.FileExists(s))
                    CopyFile(s, target);
                else if (_mount.Root.DirectoryExists(s))
                    CopyDir(s, target);
            }
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    public Task<Resp> RemoveAsync(IReadOnlyList<string> paths, User user, CancellationToken ct = default)
    {
        try
        {
            foreach (var p in paths)
            {
                var upath = Normalize(p);
                if (_mount.Root.FileExists(upath)) _mount.Root.DeleteFile(upath);
                else if (_mount.Root.DirectoryExists(upath)) _mount.Root.DeleteDirectory(upath, true);
            }
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    public Task<Resp> RemoveEmptyDirectoryAsync(string path, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        if (!_mount.Root.DirectoryExists(upath))
            return Task.FromResult(Resp.Fail("not found", RespCodes.NotFound));
        if (_mount.Root.EnumeratePaths(upath).Any())
            return Task.FromResult(Resp.Fail("directory not empty", RespCodes.BadRequest));
        _mount.Root.DeleteDirectory(upath, false);
        return Task.FromResult(Resp.Ok());
    }

    public async Task<Resp> PutStreamAsync(string path, Stream stream, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        var driver = _mount.FindDriverForPath(path);
        try
        {
            if (driver is IFileSystemUploader uploader)
            {
                await uploader.PutAsync(path, stream, ct);
            }
            else
            {
                var parent = upath.GetDirectory();
                if (parent != UPath.Root && !_mount.Root.DirectoryExists(parent))
                    _mount.Root.CreateDirectory(parent);
                using var fs = _mount.Root.OpenFile(upath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(fs, ct);
            }
            return Resp.Ok();
        }
        catch (Exception ex) { return Resp.Fail(ex.Message, RespCodes.InternalError); }
    }

    public Task<Resp> PutFormAsync(string path, Stream stream, string fileName, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemUploader uploader)
        {
            try { uploader.PutFormAsync(path, stream, fileName, ct).GetAwaiter().GetResult(); }
            catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
            return Task.FromResult(Resp.Ok());
        }
        var target = UPath.Combine(upath.GetDirectory(), fileName);
        return PutStreamAsync(target.FullName, stream, user, ct);
    }

    public Task<Resp<DirectUploadInfo>> GetDirectUploadInfoAsync(string path, long size, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemDirectUploader direct)
        {
            try
            {
                var info = direct.GetDirectUploadInfoAsync(path, size, ct).GetAwaiter().GetResult();
                return Task.FromResult(Resp<DirectUploadInfo>.Ok(new DirectUploadInfo
                {
                    Url = info.Url,
                    Headers = info.Headers,
                    Method = info.Method,
                }));
            }
            catch (Exception ex) { return Task.FromResult(Resp<DirectUploadInfo>.Fail(ex.Message, RespCodes.InternalError)); }
        }
        return Task.FromResult(Resp<DirectUploadInfo>.Fail("driver does not support direct upload", RespCodes.NotImplemented));
    }

    public Task<Resp<LinkResp>> LinkAsync(string path, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemLinkProvider linkProvider)
        {
            try
            {
                var uri = linkProvider.GetLinkAsync(path, ct).GetAwaiter().GetResult();
                if (uri is not null)
                    return Task.FromResult(Resp<LinkResp>.Ok(new LinkResp { Url = uri.ToString() }));
            }
            catch { }
        }
        return Task.FromResult(Resp<LinkResp>.Fail("link not supported", RespCodes.NotImplemented));
    }

    public Task<Resp> AddOfflineDownloadAsync(OfflineDownloadReq req, User user, CancellationToken ct = default)
        => Task.FromResult(Resp.Fail("offline download not implemented", RespCodes.NotImplemented));

    public Task<Resp<ArchiveMetaResp>> ArchiveMetaAsync(string path, User user, CancellationToken ct = default)
        => Task.FromResult(Resp<ArchiveMetaResp>.Ok(new ArchiveMetaResp
        {
            SupportedExtensions = [".zip", ".tar", ".gz", ".rar", ".7z"],
        }));

    public Task<Resp<ArchiveListResp>> ArchiveListAsync(string path, string? password, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemArchiveReader reader)
        {
            try
            {
                var entries = reader.ListArchiveEntriesAsync(path, ct).GetAwaiter().GetResult();
                var list = entries.Select(e => new ArchiveEntry
                {
                    Name = e.Name, Size = e.Size, IsDir = e.IsDir, Modified = e.Modified,
                }).ToList();
                return Task.FromResult(Resp<ArchiveListResp>.Ok(new ArchiveListResp { Content = list, Total = list.Count }));
            }
            catch (Exception ex) { return Task.FromResult(Resp<ArchiveListResp>.Fail(ex.Message, RespCodes.InternalError)); }
        }
        return Task.FromResult(Resp<ArchiveListResp>.Fail("driver does not support archive reading", RespCodes.NotImplemented));
    }

    public Task<Resp> ArchiveDecompressAsync(string archivePath, string destPath, string? password, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(archivePath);
        if (driver is IFileSystemArchiveDecompress decompressor)
        {
            try { decompressor.DecompressAsync(archivePath, destPath, password, ct).GetAwaiter().GetResult(); }
            catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
            return Task.FromResult(Resp.Ok());
        }
        return Task.FromResult(Resp.Fail("driver does not support decompression", RespCodes.NotImplemented));
    }

    public Task<Resp<DirsResp>> DirsAsync(string path, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        if (!_mount.Root.DirectoryExists(upath))
            return Task.FromResult(Resp<DirsResp>.Fail("path not found", RespCodes.NotFound));
        var dirs = new List<string>();
        CollectDirs(upath, dirs, maxDepth: 3);
        return Task.FromResult(Resp<DirsResp>.Ok(new DirsResp { Dirs = dirs }));
    }

    public Task<Resp> OtherAsync(string path, string action, User user, CancellationToken ct = default)
        => Task.FromResult(Resp.Fail($"action '{action}' not implemented", RespCodes.NotImplemented));

    public Task<Resp<SearchResp>> SearchAsync(SearchReq req, User user, CancellationToken ct = default)
    {
        var parent = Normalize(req.Parent);
        if (!_mount.Root.DirectoryExists(parent))
            return Task.FromResult(Resp<SearchResp>.Fail("parent not found", RespCodes.NotFound));
        var results = new List<Obj>();
        SearchRecursive(parent, req.Keywords, req.Recursive, results);
        var total = results.Count;
        var page = Math.Max(1, req.Page);
        var perPage = Math.Max(1, req.PerPage);
        var paged = results.Skip((page - 1) * perPage).Take(perPage).ToList();
        return Task.FromResult(Resp<SearchResp>.Ok(new SearchResp { Content = paged, Total = total }));
    }

    // --- helpers ---

    private static UPath Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return UPath.Root;
        if (!path.StartsWith('/')) path = "/" + path;
        return new UPath(path);
    }

    private static Obj ToObj(FileSystemEntry entry)
    {
        var isDir = entry is DirectoryEntry;
        return new Obj
        {
            Name = entry.Name,
            IsDir = isDir,
            Size = isDir ? 0 : (entry as FileEntry)?.Length ?? 0,
            Modified = entry.LastWriteTime,
            Created = entry.CreationTime,
            Type = isDir ? 1 : 0,
        };
    }

    private void CollectDirs(UPath path, List<string> dirs, int maxDepth)
    {
        if (maxDepth <= 0) return;
        foreach (var p in _mount.Root.EnumeratePaths(path))
        {
            if (!_mount.Root.DirectoryExists(p)) continue;
            dirs.Add(p.FullName);
            CollectDirs(p, dirs, maxDepth - 1);
        }
    }

    private void SearchRecursive(UPath path, string keywords, bool recursive, List<Obj> results)
    {
        foreach (var p in _mount.Root.EnumeratePaths(path))
        {
            FileSystemEntry entry = _mount.Root.FileExists(p)
                ? _mount.Root.GetFileEntry(p)
                : _mount.Root.GetDirectoryEntry(p);
            if (entry.Name.Contains(keywords, StringComparison.OrdinalIgnoreCase))
                results.Add(ToObj(entry));
            if (entry is DirectoryEntry && recursive)
                SearchRecursive(p, keywords, recursive, results);
        }
    }

    private void CopyFile(UPath src, UPath dst)
    {
        using var sIn = _mount.Root.OpenFile(src, FileMode.Open, FileAccess.Read);
        using var sOut = _mount.Root.OpenFile(dst, FileMode.Create, FileAccess.Write);
        sIn.CopyTo(sOut);
    }

    private void CopyDir(UPath src, UPath dst)
    {
        _mount.Root.CreateDirectory(dst);
        foreach (var entry in _mount.Root.EnumeratePaths(src))
        {
            var target = UPath.Combine(dst, entry.GetName());
            if (_mount.Root.FileExists(entry)) CopyFile(entry, target);
            else if (_mount.Root.DirectoryExists(entry)) CopyDir(entry, target);
        }
    }
}
