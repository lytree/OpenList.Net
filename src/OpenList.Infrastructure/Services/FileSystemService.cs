using System.Text.RegularExpressions;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using Zio;
using DirectUploadInfo = OpenList.Core.Interfaces.DirectUploadInfo;
using ArchiveEntry = OpenList.Core.Interfaces.ArchiveEntry;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 文件系统操作服务，统一经过 <see cref="ZioMountService"/> 的 <see cref="MountFileSystem"/> 路由。
/// 对应 Go 参考实现 <c>internal/fs</c>。操作基于路径；挂载服务负责解析底层驱动。
/// 可选驱动能力（链接/上传/归档）通过 <see cref="OpenList.Core.Drivers"/> 中的扩展接口探测。
/// </summary>
public sealed class FileSystemService : IFileSystemService
{
    private readonly ZioMountService _mount;
    private readonly ISignService? _signs;
    private readonly IOfflineDownloadService? _offline;

    /// <summary>创建文件系统服务实例。</summary>
    public FileSystemService(ZioMountService mount) => _mount = mount;

    /// <summary>
    /// 创建文件系统服务实例（带签名服务，用于在 GetAsync 中生成 raw_url）。
    /// </summary>
    /// <param name="mount">挂载服务。</param>
    /// <param name="signs">签名服务。</param>
    public FileSystemService(ZioMountService mount, ISignService signs)
    {
        _mount = mount;
        _signs = signs;
    }

    /// <summary>
    /// 创建文件系统服务实例（带离线下载编排支持）。
    /// </summary>
    /// <param name="mount">挂载服务。</param>
    /// <param name="signs">签名服务（可选）。</param>
    /// <param name="offline">离线下载服务（可选）。</param>
    public FileSystemService(ZioMountService mount, ISignService? signs, IOfflineDownloadService? offline)
    {
        _mount = mount;
        _signs = signs;
        _offline = offline;
    }

    /// <summary>
    /// 列出目录下的条目，目录优先、名称字典序，并应用分页。
    /// </summary>
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
        // 镜像 Go 的 PageReq.Validate()：page<1 → 1；per_page<1 → int.MaxValue（返回全部）。
        // 前端可能省略 per_page（默认 0），此时应返回全部条目。
        var page = Math.Max(1, req.Page);
        var perPage = req.PerPage < 1 ? int.MaxValue : req.PerPage;
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

    /// <summary>
    /// 获取文件或目录的详情。对于文件，若配置了签名服务则额外生成指向签名代理端点的 raw_url。
    /// </summary>
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
        // 对文件生成指向签名 /p/ 代理端点的 raw_url。
        // 这是前端用作视频/音频/pdf 预览的媒体源。
        if (isFile && _signs is not null)
        {
            resp.RawUrl = BuildRawUrl(req.Path);
        }
        return Task.FromResult(Resp<FsGetResp>.Ok(resp));
    }

    /// <summary>
    /// 构建签名代理 URL：<c>/p/{path}?sign={hmac}</c>。路径段逐个进行 URL 编码，
    /// 以保留 '/' 分隔符（ASP.NET 通配路由所需）。
    /// </summary>
    private string BuildRawUrl(string path)
    {
        var sign = _signs!.Sign(path);
        // 逐段编码以保留 '/' 作为路径分隔符。
        // 前导 '/' 产生一个空首段，将其去掉以避免双斜杠。
        var segments = path.Split('/').SkipWhile(string.IsNullOrEmpty).Select(Uri.EscapeDataString);
        var encoded = string.Join('/', segments);
        return $"/p/{encoded}?sign={Uri.EscapeDataString(sign)}";
    }

    /// <summary>新建目录；若已存在返回冲突。</summary>
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

    /// <summary>重命名/移动文件或目录。</summary>
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

    /// <summary>批量重命名；任一项失败即中止并返回该错误。</summary>
    public Task<Resp> BatchRenameAsync(IReadOnlyList<BatchRenameItem> items, User user, CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            var r = RenameAsync(item.SrcName, item.NewName, user, ct).GetAwaiter().GetResult();
            if (r.Code != 200) return Task.FromResult(r);
        }
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 正则批量重命名：在 <paramref name="srcDir"/> 下匹配 <paramref name="srcReg"/> 的文件，
    /// 用 <paramref name="newReg"/> 替换生成新名后依次重命名。
    /// </summary>
    public Task<Resp> RegexRenameAsync(string srcDir, string srcReg, string newReg, User user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(srcReg))
            return Task.FromResult(Resp.Fail("src_name_regex is required", RespCodes.BadRequest));
        Regex regex;
        try { regex = new Regex(srcReg); }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }

        var dir = Normalize(srcDir);
        if (!_mount.Root.DirectoryExists(dir))
            return Task.FromResult(Resp.Fail("src_dir not found", RespCodes.NotFound));

        try
        {
            var items = new List<BatchRenameItem>();
            foreach (var p in _mount.Root.EnumeratePaths(dir))
            {
                var name = p.GetName();
                if (!regex.IsMatch(name)) continue;
                var newName = regex.Replace(name, newReg);
                if (string.IsNullOrEmpty(newName) || newName == name) continue;
                items.Add(new BatchRenameItem
                {
                    SrcName = UPath.Combine(dir, name).FullName,
                    NewName = UPath.Combine(dir, newName).FullName,
                });
            }
            foreach (var item in items)
            {
                var r = RenameAsync(item.SrcName, item.NewName, user, ct).GetAwaiter().GetResult();
                if (r.Code != 200) return Task.FromResult(r);
            }
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    /// <summary>把多个源路径移动到目标目录下。</summary>
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

    /// <summary>递归移动（单源移动的别名）。</summary>
    public Task<Resp> RecursiveMoveAsync(string srcPath, string destDir, User user, CancellationToken ct = default)
        => MoveAsync([srcPath], destDir, user, ct);

    /// <summary>把多个源路径复制到目标目录下（文件与目录均支持递归）。</summary>
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

    /// <summary>删除多个路径（目录递归删除）。</summary>
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

    /// <summary>删除空目录；目录非空或不存在时返回错误。</summary>
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

    /// <summary>
    /// 直接写入流内容到指定路径。若底层驱动实现了上传接口则走驱动，否则在根文件系统上写普通文件。
    /// </summary>
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

    /// <summary>
    /// 表单上传：按 <paramref name="path"/> 定位驱动（若有上传接口则走驱动），否则把流写入
    /// <paramref name="path"/> 目录下的 <paramref name="fileName"/>。
    /// </summary>
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

    /// <summary>
    /// 获取直传上传信息（适用于支持服务端直传的驱动，如对象存储预签名）。
    /// </summary>
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

    /// <summary>生成路径的分享链接（仅当驱动的链接提供接口存在时）。</summary>
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

    /// <summary>把请求转交给离线下载服务，未配置时返回 501。</summary>
    public async Task<Resp> AddOfflineDownloadAsync(OfflineDownloadReq req, User user, CancellationToken ct = default)
    {
        if (_offline is null)
            return Resp.Fail("offline download not configured", RespCodes.NotImplemented);
        var r = await _offline.AddAsync(req, user, ct);
        return r.Code == 200 ? Resp.Ok() : Resp.Fail(r.Message, r.Code);
    }

    /// <summary>返回已支持的归档文件扩展名列表。</summary>
    public Task<Resp<ArchiveMetaResp>> ArchiveMetaAsync(string path, User user, CancellationToken ct = default)
        => Task.FromResult(Resp<ArchiveMetaResp>.Ok(new ArchiveMetaResp
        {
            SupportedExtensions = [".zip", ".tar", ".gz", ".rar", ".7z"],
        }));

    /// <summary>列举归档文件内部条目（要求驱动实现归档读取接口）。</summary>
    public Task<Resp<ArchiveListResp>> ArchiveListAsync(string path, string? password, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemArchiveReader reader)
        {
            try
            {
                // 驱动的 System 以其挂载点为根，因此传入相对挂载点的路径。
                var relPath = _mount.ToDriverRelativePath(path);
                var entries = reader.ListArchiveEntriesAsync(relPath, ct).GetAwaiter().GetResult();
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

    /// <summary>把归档文件解压到目标目录（要求驱动实现归档解压接口）。</summary>
    public Task<Resp> ArchiveDecompressAsync(string archivePath, string destPath, string? password, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(archivePath);
        if (driver is IFileSystemArchiveDecompress decompressor)
        {
            try
            {
                // 驱动的 System 以其挂载点为根，因此传入相对挂载点的路径。
                var relArchive = _mount.ToDriverRelativePath(archivePath);
                var relDest = _mount.ToDriverRelativePath(destPath);
                decompressor.DecompressAsync(relArchive, relDest, password, ct).GetAwaiter().GetResult();
            }
            catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
            return Task.FromResult(Resp.Ok());
        }
        return Task.FromResult(Resp.Fail("driver does not support decompression", RespCodes.NotImplemented));
    }

    /// <summary>递归收集目录（最深 3 层），用于前端目录选择器。</summary>
    public Task<Resp<DirsResp>> DirsAsync(string path, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        if (!_mount.Root.DirectoryExists(upath))
            return Task.FromResult(Resp<DirsResp>.Fail("path not found", RespCodes.NotFound));
        var dirs = new List<string>();
        CollectDirs(upath, dirs, maxDepth: 3);
        return Task.FromResult(Resp<DirsResp>.Ok(new DirsResp { Dirs = dirs }));
    }

    /// <summary>杂项文件系统动作（当前未实现）。</summary>
    public Task<Resp> OtherAsync(string path, string action, User user, CancellationToken ct = default)
        => Task.FromResult(Resp.Fail($"action '{action}' not implemented", RespCodes.NotImplemented));

    /// <summary>按关键字递归（或仅当前层）搜索文件系统，返回分页结果。</summary>
    public Task<Resp<SearchResp>> SearchAsync(SearchReq req, User user, CancellationToken ct = default)
    {
        var parent = Normalize(req.Parent);
        if (!_mount.Root.DirectoryExists(parent))
            return Task.FromResult(Resp<SearchResp>.Fail("parent not found", RespCodes.NotFound));
        var results = new List<Obj>();
        SearchRecursive(parent, req.Keywords, req.Recursive, results);
        var total = results.Count;
        // 镜像 Go 的 PageReq.Validate()：page<1 → 1；per_page<1 → int.MaxValue（返回全部）。
        var page = Math.Max(1, req.Page);
        var perPage = req.PerPage < 1 ? int.MaxValue : req.PerPage;
        var paged = results.Skip((page - 1) * perPage).Take(perPage).ToList();
        var nodes = paged.ConvertAll(o => new SearchNode
        {
            Parent = UPath.Combine(parent, "").ToString(),
            Name = o.Name,
            IsDir = o.IsDir,
            Size = o.Size,
            Path = UPath.Combine(parent, o.Name).ToString(),
            Type = o.Type,
        });
        return Task.FromResult(Resp<SearchResp>.Ok(new SearchResp { Content = nodes, Total = total }));
    }

    // --- 辅助方法 ---

    /// <summary>把路径规整为 Zio 的 UPath（补上前导 /）。</summary>
    private static UPath Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return UPath.Root;
        if (!path.StartsWith('/')) path = "/" + path;
        return new UPath(path);
    }

    /// <summary>把 Zio 文件系统条目转为对外对象（区分文件/目录并推断类型）。</summary>
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
            Type = GetObjType(entry.Name, isDir),
        };
    }

    // 镜像 Go 的 conf const iota：UNKNOWN=0, FOLDER=1, VIDEO=2, AUDIO=3, TEXT=4, IMAGE=5。
    // 前端依据 obj.type 选择预览组件（视频播放器、图片查看器等）。
    private const int TypeUnknown = 0;
    private const int TypeFolder = 1;
    private const int TypeVideo = 2;
    private const int TypeAudio = 3;
    private const int TypeText = 4;
    private const int TypeImage = 5;

    // 取自 Go 的 internal/bootstrap/data/setting.go 的默认扩展名列表。
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "flac", "ogg", "m4a", "wav", "opus", "wma" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "avi", "mov", "rmvb", "webm", "flv", "m3u8" };
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "tiff", "jpeg", "png", "gif", "bmp", "svg", "ico", "swf", "webp", "avif" };
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "htm", "html", "xml", "java", "properties", "sql", "js", "md", "json", "conf", "ini", "vue", "php", "py", "bat", "gitignore", "yml", "go", "sh", "c", "cpp", "h", "hpp", "tsx", "vtt", "srt", "ass", "rs", "lrc", "strm" };

    /// <summary>按文件扩展名推断文件类型。</summary>
    private static int GetFileType(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return TypeUnknown;
        var ext = name[(dot + 1)..];
        if (AudioExts.Contains(ext)) return TypeAudio;
        if (VideoExts.Contains(ext)) return TypeVideo;
        if (ImageExts.Contains(ext)) return TypeImage;
        if (TextExts.Contains(ext)) return TypeText;
        return TypeUnknown;
    }

    /// <summary>目录恒为 TypeFolder，文件类型按扩展名推断。</summary>
    private static int GetObjType(string name, bool isDir)
        => isDir ? TypeFolder : GetFileType(name);

    /// <summary>递归收集目录（受最大深度限制）。</summary>
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

    /// <summary>递归（或仅当前层）按关键字匹配名称并收集命中对象。</summary>
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

    /// <summary>复制单个文件内容。</summary>
    private void CopyFile(UPath src, UPath dst)
    {
        using var sIn = _mount.Root.OpenFile(src, FileMode.Open, FileAccess.Read);
        using var sOut = _mount.Root.OpenFile(dst, FileMode.Create, FileAccess.Write);
        sIn.CopyTo(sOut);
    }

    /// <summary>递归复制目录。</summary>
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