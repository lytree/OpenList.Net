using System.IO.Compression;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class FsEndpoints
{
    public static IEndpointRouteBuilder MapFsApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/fs");

        // fs/list - supports both GET and POST, allows guest (Go: Auth(true))
        group.MapMethods("list", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IMetaService metaSvc, ISharingService sharings, HttpContext ctx, ListReq req) =>
        {
            // Support sharing path: /@s prefix routes to sharing logic
            if (req.Path.StartsWith("/@s", StringComparison.Ordinal))
            {
                var sharingPath = req.Path[3..]; // strip /@s
                var (sid, subPath) = SplitSharingPath(sharingPath);
                if (string.IsNullOrEmpty(sid))
                    return Results.Json(Resp<ListResp>.Error(400, "invalid share id"));

                var (sharing, sharingObjs) = await sharings.ListSharingObjectsAsync(sid, subPath, req.Password, req.Refresh);
                if (sharing == null) return Results.Json(Resp<ListResp>.Error(404, "share not found"));
                if (!sharing.Valid()) return Results.Json(Resp<ListResp>.Error(403, "share expired or disabled"));
                if (!sharing.Verify(req.Password)) return Results.Json(Resp<ListResp>.Error(403, "wrong password"));

                var sAllItems = sharingObjs.Select(MapObj).ToList();
                var sTotal = sAllItems.Count;
                if (req.Page < 1) req.Page = 1;
                if (req.PerPage < 1) req.PerPage = int.MaxValue;
                var sStart = (req.Page - 1) * req.PerPage;
                var sPaged = sStart >= sTotal ? [] : sAllItems.Skip(sStart).Take(req.PerPage).ToList();

                return Results.Json(new Resp<ListResp>(new ListResp
                {
                    Content = sPaged,
                    Total = sTotal,
                    Readme = sharing.Readme,
                    Header = sharing.Header,
                    Provider = "unknown"
                }));
            }

            // Password check (Go: common.CanAccess)
            var meta = await metaSvc.GetNearestMetaAsync(req.Path);
            if (meta?.Password != null && !UserCanAccessWithoutPassword(ctx))
            {
                if (!MetaCoversPath(meta.Path, req.Path, meta.PSub) || meta.Password != req.Password)
                    return Results.Json(Resp<ListResp>.Error(403, "password is incorrect or you have no permission"));
            }

            // Validate pagination (Go: PageReq.Validate)
            if (req.Page < 1) req.Page = 1;
            if (req.PerPage < 1) req.PerPage = int.MaxValue;

            var args = new ListArgs { ReqPath = req.Path, Refresh = req.Refresh, WithStorageDetails = req.WithStorageDetails };
            var objs = await fs.ListAsync(req.Path, args);
            var allItems = objs.Select(MapObj).ToList();
            var total = allItems.Count;

            // Apply pagination (Go: pagination function)
            var start = (req.Page - 1) * req.PerPage;
            var paged = start >= total ? [] : allItems.Skip(start).Take(req.PerPage).ToList();

            // Add meta info to response (Go: readme, header, write from meta)
            var resp = new ListResp { Content = paged, Total = total };
            if (meta != null)
            {
                if (meta.Readme != null && (meta.Path == req.Path || meta.RSub)) resp.Readme = meta.Readme;
                if (meta.Header != null && (meta.Path == req.Path || meta.HeaderSub)) resp.Header = meta.Header;
                if (meta.Write && (meta.Path == req.Path || meta.WSub)) resp.Write = true;
            }

            // Add direct upload tools if driver supports it (Go: direct_upload_tools)
            var storage = await fs.GetStorageAsync(req.Path);
            if (storage != null)
            {
                var driver = storageService.GetDriverByMountPath(storage.MountPath);
                if (driver is IDriverDirectUploader directUploader)
                    resp.DirectUploadTools = directUploader.GetDirectUploadTools();
            }

            return Results.Json(new Resp<ListResp>(resp));
        }).AllowAnonymous();

        // fs/get - supports both GET and POST, allows guest (Go: Auth(true))
        group.MapMethods("get", ["GET", "POST"], async (IFileSystemService fs, IMetaService metaSvc, ISignService sign, ISharingService sharings, HttpContext ctx, GetReq req) =>
        {
            // Support sharing path: /@s prefix routes to sharing logic
            if (req.Path.StartsWith("/@s", StringComparison.Ordinal))
            {
                var sharingPath = req.Path[3..]; // strip /@s
                var (sid, subPath) = SplitSharingPath(sharingPath);
                if (string.IsNullOrEmpty(sid))
                    return Results.Json(Resp<GetResp>.Error(400, "invalid share id"));

                var (sharing, sObj) = await sharings.GetSharingObjectAsync(sid, subPath, req.Password);
                if (sharing == null) return Results.Json(Resp<GetResp>.Error(404, "share not found"));
                if (!sharing.Valid()) return Results.Json(Resp<GetResp>.Error(403, "share expired or disabled"));
                if (!sharing.Verify(req.Password)) return Results.Json(Resp<GetResp>.Error(403, "wrong password"));
                if (sObj == null) return Results.Json(Resp<GetResp>.Error(404, "object not found"));

                var sResp = MapGetResp(sObj);
                if (!sObj.IsDir())
                {
                    var fakePath = $"/{sid}/{subPath}".TrimEnd('/');
                    sResp.RawURL = $"/sd{fakePath}";
                    if (!string.IsNullOrEmpty(sharing.Pwd)) sResp.RawURL += $"?pwd={sharing.Pwd}";
                }
                sResp.Readme = sharing.Readme;
                sResp.Provider = "unknown";
                return Results.Json(new Resp<GetResp>(sResp));
            }

            // Password check (Go: common.CanAccess)
            var meta = await metaSvc.GetNearestMetaAsync(req.Path);
            if (meta?.Password != null && !UserCanAccessWithoutPassword(ctx))
            {
                if (!MetaCoversPath(meta.Path, req.Path, meta.PSub) || meta.Password != req.Password)
                    return Results.Json(Resp<GetResp>.Error(403, "password is incorrect or you have no permission"));
            }

            var obj = await fs.GetAsync(req.Path);
            if (obj == null) return Results.Json(Resp<GetResp>.Error(404, "object not found"));
            var resp = MapGetResp(obj);
            if (!obj.IsDir())
            {
                var link = await fs.LinkAsync(req.Path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
                if (link != null)
                {
                    resp.RawURL = link.URL;
                }
            }
            return Results.Json(new Resp<GetResp>(resp));
        }).AllowAnonymous();

        // Archive endpoints (allows guest, Go: Auth(true))
        group.MapMethods("archive/meta", ["GET", "POST"], async (IFileSystemService fs, ArchiveMetaReq req) =>
        {
            var obj = await fs.GetAsync(req.Path);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "file not found"));
            if (obj.IsDir()) return Results.Json(Resp<object>.Error(400, "path is a directory"));

            var archiveEntries = await ReadArchiveEntriesAsync(fs, req.Path, req.Password);
            if (archiveEntries == null)
                return Results.Json(Resp<object>.Error(500, "failed to read archive"));

            var isEncrypted = archiveEntries.Any(e => e.IsEncrypted);
            return Results.Json(new Resp<ArchiveMetaResp>(new ArchiveMetaResp
            {
                Comment = "",
                IsEncrypted = isEncrypted,
                Content = archiveEntries.Select(e => new ArchiveEntryResp
                {
                    Name = e.Name,
                    Size = e.Size,
                    IsDir = e.IsDir
                }).ToList()
            }));
        }).RequireAuthorization();

        group.MapMethods("archive/list", ["GET", "POST"], async (IFileSystemService fs, ArchiveListReq req) =>
        {
            var obj = await fs.GetAsync(req.Path);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "file not found"));
            if (obj.IsDir()) return Results.Json(Resp<object>.Error(400, "path is a directory"));

            var archiveEntries = await ReadArchiveEntriesAsync(fs, req.Path, req.Password);
            if (archiveEntries == null)
                return Results.Json(Resp<object>.Error(500, "failed to read archive"));

            // Filter by inner path if specified
            IEnumerable<ArchiveEntryInfo> filtered = archiveEntries;
            if (!string.IsNullOrEmpty(req.InnerPath))
            {
                var prefix = req.InnerPath.TrimStart('/');
                filtered = filtered.Where(e => e.Name.StartsWith(prefix) && e.Name != prefix);
            }

            // Show only direct children (not nested)
            var entries = filtered.ToList();
            var result = new List<ArchiveEntryResp>();
            foreach (var entry in entries)
            {
                var relativePath = entry.Name;
                if (!string.IsNullOrEmpty(req.InnerPath))
                {
                    var prefix = req.InnerPath.TrimStart('/');
                    if (relativePath.StartsWith(prefix))
                        relativePath = relativePath[prefix.Length..].TrimStart('/');
                }

                var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                // Only add direct children
                if (parts.Length == 1 || (parts.Length > 1 && !result.Any(r => r.Name == parts[0])))
                {
                    result.Add(new ArchiveEntryResp
                    {
                        Name = parts[0],
                        Size = parts.Length == 1 ? entry.Size : 0,
                        IsDir = parts.Length > 1
                    });
                }
            }

            // Pagination
            var page = int.TryParse(req.Page, out var p) ? p : 1;
            var perPage = int.TryParse(req.PerPage, out var pp) ? pp : 0;
            if (perPage > 0)
                result = result.Skip((page - 1) * perPage).Take(perPage).ToList();

            return Results.Json(new Resp<ArchiveListResp>(new ArchiveListResp
            {
                Content = result,
                Total = result.Count
            }));
        }).RequireAuthorization();

        // Write operations (requires auth, not guest)
        group.MapPost("mkdir", async (IFileSystemService fs, MkdirReq req) =>
        {
            await fs.MakeDirAsync(req.Path);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("rename", async (IFileSystemService fs, RenameReq req) =>
        {
            await fs.RenameAsync(req.Path, req.Name);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("batch_rename", async (IFileSystemService fs, BatchRenameReq req) =>
        {
            foreach (var item in req.RenameObjects ?? [])
                await fs.RenameAsync(item.SrcPath, item.NewName);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("regex_rename", async (IFileSystemService fs, RegexRenameReq req) =>
        {
            var objs = await fs.ListAsync(req.SrcDir);
            foreach (var obj in objs)
            {
                var name = obj.GetName();
                try
                {
                    var newName = Regex.Replace(name, req.Regex, req.Replace);
                    if (newName != name)
                        await fs.RenameAsync($"{req.SrcDir}/{name}", newName);
                }
                catch { /* skip invalid regex matches */ }
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("move", async (IFileSystemService fs, MoveReq req) =>
        {
            if (req.Names != null)
            {
                foreach (var name in req.Names)
                    await fs.MoveAsync($"{req.SrcDir}/{name}", req.DstDir);
            }
            else
            {
                await fs.MoveAsync(req.SrcDir, req.DstDir);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("recursive_move", async (IFileSystemService fs, RecursiveMoveReq req) =>
        {
            foreach (var srcDir in req.SrcDirs ?? [])
                await fs.MoveAsync(srcDir, req.DstDir);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("copy", async (IFileSystemService fs, CopyReq req) =>
        {
            if (req.Names != null)
            {
                foreach (var name in req.Names)
                    await fs.CopyAsync($"{req.SrcDir}/{name}", req.DstDir);
            }
            else
            {
                await fs.CopyAsync(req.SrcDir, req.DstDir);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("remove", async (IFileSystemService fs, RemoveReq req) =>
        {
            if (req.Names != null)
            {
                foreach (var name in req.Names)
                    await fs.RemoveAsync($"{req.Dir}/{name}");
            }
            else
            {
                await fs.RemoveAsync(req.Dir);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("remove_empty_directory", async (IFileSystemService fs, RemoveEmptyDirReq req) =>
        {
            var objs = await fs.ListAsync(req.Path);
            foreach (var obj in objs.Where(o => o.IsDir()))
            {
                var subObjs = await fs.ListAsync(obj.GetPath());
                if (!subObjs.Any())
                    await fs.RemoveAsync(obj.GetPath());
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        // PUT /fs/put - stream upload (Go: FsStream, uses File-Path header)
        group.MapPut("put", async (IFileSystemService fs, HttpContext ctx) =>
        {
            // Go: path from File-Path header, URL-decoded
            var filePath = ctx.Request.Headers["File-Path"].FirstOrDefault() ?? "";
            filePath = Uri.UnescapeDataString(filePath);
            if (string.IsNullOrEmpty(filePath))
                return Results.Json(Resp<object>.Error(400, "File-Path header is required"));

            // Go: overwrite check (default true)
            var overwrite = ctx.Request.Headers["Overwrite"].FirstOrDefault() != "false";
            if (!overwrite)
            {
                var existing = await fs.GetAsync(filePath);
                if (existing != null)
                    return Results.Json(Resp<object>.Error(403, "file exists"));
            }

            // Go: size from Content-Length or X-File-Size header
            var size = ctx.Request.ContentLength ?? -1;
            if (size < 0)
            {
                var sizeStr = ctx.Request.Headers["X-File-Size"].FirstOrDefault();
                if (!string.IsNullOrEmpty(sizeStr) && long.TryParse(sizeStr, out var s))
                    size = s;
            }

            // Go: last-modified from header (millisecond timestamp)
            var lastModified = DateTime.UtcNow;
            var lmStr = ctx.Request.Headers["Last-Modified"].FirstOrDefault();
            if (!string.IsNullOrEmpty(lmStr) && long.TryParse(lmStr, out var lmMs))
                lastModified = DateTime.UnixEpoch.AddMilliseconds(lmMs);

            var dir = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "/";
            var fileName = Path.GetFileName(filePath);

            if (ctx.Request.ContentLength > 0 || size != 0)
            {
                var obj = new ObjItem { Name = fileName, Size = size >= 0 ? size : 0, Modified = lastModified };
                var fileStream = new FileStreamer
                {
                    Stream = ctx.Request.Body,
                    Obj = obj,
                    Size = size >= 0 ? size : 0
                };
                await fs.PutAsync(dir, fileStream, _ => { });
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        // PUT /fs/form - form upload (Go: FsForm, uses File-Path header)
        group.MapPut("form", async (IFileSystemService fs, HttpContext ctx) =>
        {
            // Go: path from File-Path header
            var filePath = ctx.Request.Headers["File-Path"].FirstOrDefault() ?? "";
            filePath = Uri.UnescapeDataString(filePath);
            if (string.IsNullOrEmpty(filePath))
                return Results.Json(Resp<object>.Error(400, "File-Path header is required"));

            var overwrite = ctx.Request.Headers["Overwrite"].FirstOrDefault() != "false";
            if (!overwrite)
            {
                var existing = await fs.GetAsync(filePath);
                if (existing != null)
                    return Results.Json(Resp<object>.Error(403, "file exists"));
            }

            if (!ctx.Request.HasFormContentType)
                return Results.Json(Resp<object>.Error(400, "expected multipart form data"));

            var dir = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "/";
            var form = await ctx.Request.ReadFormAsync();
            foreach (var file in form.Files)
            {
                var obj = new ObjItem { Name = file.FileName, Size = file.Length };
                var fileStream = new FileStreamer
                {
                    Stream = file.OpenReadStream(),
                    Obj = obj,
                    Size = file.Length
                };
                await fs.PutAsync(dir, fileStream, _ => { });
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        // fs/link - admin only (Go: middlewares.AuthAdmin)
        group.MapPost("link", async (IFileSystemService fs, HttpContext ctx, LinkReq req) =>
        {
            var link = await fs.LinkAsync(req.Path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
            if (link == null) return Results.Json(Resp<LinkResp>.Error(500, "failed to get link"));
            return Results.Json(new Resp<LinkResp>(new LinkResp { URL = link.URL, Header = link.Header }));
        }).RequireAuthorization(policy => policy.RequireRole("2"));

        // fs/search - requires auth + search index enabled
        group.MapMethods("search", ["GET", "POST"], async (ISearchService search, IConfiguration config, SearchReq req) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });
            var results = await search.SearchAsync(req.Parent, req.Keywords, req.Page, req.PerPage);
            // Scope filter: 0=all, 1=dir only, 2=file only
            if (req.Scope > 0)
                results = results.Where(r => req.Scope == 1 ? r.IsDir : !r.IsDir).ToList();
            return Results.Json(new Resp<SearchResp>(new SearchResp { Content = results, Total = results.Count }));
        }).RequireAuthorization();

        // fs/dirs - requires auth
        group.MapMethods("dirs", ["GET", "POST"], async (IFileSystemService fs, DirsReq req) =>
        {
            var objs = await fs.ListAsync(req.Path);
            var dirs = objs.Where(o => o.IsDir()).Select(MapObj).ToList();
            return Results.Json(new Resp<DirsResp>(new DirsResp { Content = dirs }));
        }).RequireAuthorization();

        // fs/other - requires auth
        group.MapMethods("other", ["GET", "POST"], async (IFileSystemService fs, OtherReq req) =>
        {
            var obj = await fs.GetAsync(req.Path);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "object not found"));
            var result = await fs.OtherAsync(new OtherArgs { Obj = obj, Method = req.Method, Data = req.Data });
            return Results.Json(new Resp<object>(result));
        }).RequireAuthorization();

        group.MapPost("add_offline_download", async (OfflineDownloadService downloadService, AddOfflineDownloadReq req, CancellationToken ct) =>
        {
            try
            {
                var tasks = await downloadService.AddUrlsAsync(req.Urls ?? [], req.DstDir, req.Tool, ct);
                return Results.Json(new Resp<object>(new { tasks = tasks.Select(t => new { t.Id, t.Description, t.State }).ToList() }));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(Resp<object>.Error(400, ex.Message));
            }
        }).RequireAuthorization();

        group.MapPost("archive/decompress", async (IFileSystemService fs, ITaskService taskService, ArchiveDecompressReq req) =>
        {
            var task = await taskService.CreateTaskAsync("decompress", $"Decompress {req.SrcDir}/{req.Name}", async (taskId, ct) =>
            {
                var srcPath = string.IsNullOrEmpty(req.SrcDir) || req.SrcDir == "/"
                    ? $"/{req.Name}"
                    : $"{req.SrcDir}/{req.Name}";
                var link = await fs.LinkAsync(srcPath, new LinkArgs());
                if (link == null) return;

                Stream? archiveStream = null;
                string? tempFile = null;

                try
                {
                    if (!string.IsNullOrEmpty(link.URL) && File.Exists(link.URL))
                    {
                        archiveStream = new FileStream(link.URL, FileMode.Open, FileAccess.Read);
                    }
                    else if (!string.IsNullOrEmpty(link.URL) && link.URL.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        using var http = new HttpClient();
                        var response = await http.GetAsync(link.URL, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        tempFile = Path.Combine(Path.GetTempPath(), $"openlist-decompress-{Guid.NewGuid():N}");
                        using (var fs2 = File.Create(tempFile))
                            await response.Content.CopyToAsync(fs2, ct);
                        archiveStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read);
                    }
                    else return;

                    var ext = Path.GetExtension(req.Name).ToLowerInvariant();
                    var dstDir = string.IsNullOrEmpty(req.DstDir) ? req.SrcDir : req.DstDir;

                    if (ext == ".zip")
                    {
                        using var archive = new System.IO.Compression.ZipArchive(archiveStream, System.IO.Compression.ZipArchiveMode.Read);
                        var extractDir = req.PutIntoNewDir
                            ? Path.Combine(dstDir, Path.GetFileNameWithoutExtension(req.Name))
                            : dstDir;

                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.EndsWith('/')) continue; // skip directories
                            var destPath = Path.Combine(extractDir, entry.FullName);
                            var destDir2 = Path.GetDirectoryName(destPath);
                            if (destDir2 != null) Directory.CreateDirectory(destDir2);

                            if (!req.Overwrite && File.Exists(destPath)) continue;
                            entry.ExtractToFile(destPath, req.Overwrite);
                        }
                    }
                    // TODO: .tar.gz, .rar, .7z support
                }
                catch { /* log error */ }
                finally
                {
                    archiveStream?.Dispose();
                    if (tempFile != null && File.Exists(tempFile))
                        try { File.Delete(tempFile); } catch { }
                }
            });
            return Results.Json(new Resp<object>(new { task_id = task.Id }));
        }).RequireAuthorization();

        // Torrent endpoints
        group.MapPost("torrent/parse", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/upload_parse", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/rapid_upload", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/generate", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();

        // Direct upload info
        group.MapPost("get_direct_upload_info", async (IFileSystemService fs, IStorageService storageService, DirectUploadReq req) =>
        {
            var dirPath = req.Path ?? "/";
            var dirObj = await fs.GetAsync(dirPath);
            if (dirObj == null || !dirObj.IsDir())
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "directory not found" });

            // Get the storage and driver for this path
            var storage = await fs.GetStorageAsync(dirPath);
            if (storage != null)
            {
                var driver = storageService.GetDriverByMountPath(storage.MountPath);
                if (driver is IDriverDirectUploader directUploader)
                {
                    var info = await directUploader.GetDirectUploadInfoAsync(req.Tool ?? "", dirObj, req.FileName ?? "", req.FileSize ?? 0);
                    return Results.Json(new Resp<object?>(info));
                }
            }

            return Results.Json(new Resp<object?>(null) { Code = 500, Message = "driver does not support direct upload" });
        }).RequireAuthorization();

        return endpoints;
    }

    private static ObjResp MapObj(IObj obj) => new()
    {
        Name = obj.GetName(),
        Size = obj.GetSize(),
        IsDir = obj.IsDir(),
        Modified = obj.GetModTime(),
        Created = obj.GetCreateTime(),
        HashInfoStr = obj.GetHash(),
        ID = obj.GetID(),
        Path = obj.GetPath()
    };

    private static GetResp MapGetResp(IObj obj) => new()
    {
        Name = obj.GetName(),
        Size = obj.GetSize(),
        IsDir = obj.IsDir(),
        Modified = obj.GetModTime(),
        Created = obj.GetCreateTime(),
        HashInfoStr = obj.GetHash(),
        ID = obj.GetID(),
        Path = obj.GetPath()
    };

    private static bool UserCanAccessWithoutPassword(HttpContext ctx)
    {
        var user = ctx.Items["User"] as User;
        if (user != null) return user.HasPermission(UserPermission.CanAccessWithoutPassword);
        // Check from claims for authenticated users
        var permStr = ctx.User.FindFirst("Permission")?.Value;
        if (int.TryParse(permStr, out var perm))
            return (perm & (int)UserPermission.CanAccessWithoutPassword) != 0;
        return false;
    }

    private static bool MetaCoversPath(string metaPath, string targetPath, bool sub)
    {
        if (metaPath == targetPath) return true;
        if (!sub) return false;
        return targetPath.StartsWith(metaPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static (string sid, string path) SplitSharingPath(string sharingPath)
    {
        var trimmed = sharingPath.TrimStart('/');
        var slashIdx = trimmed.IndexOf('/');
        return slashIdx < 0
            ? (trimmed, "")
            : (trimmed[..slashIdx], trimmed[(slashIdx + 1)..]);
    }

    private static async Task<List<ArchiveEntryInfo>?> ReadArchiveEntriesAsync(IFileSystemService fs, string path, string? password)
    {
        var link = await fs.LinkAsync(path, new LinkArgs());
        if (link == null) return null;

        Stream? archiveStream = null;
        string? tempFile = null;

        try
        {
            // Try to open as local file first
            if (!string.IsNullOrEmpty(link.URL) && File.Exists(link.URL))
            {
                archiveStream = new FileStream(link.URL, FileMode.Open, FileAccess.Read);
            }
            else if (!string.IsNullOrEmpty(link.URL) && link.URL.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Download to temp file
                using var http = new HttpClient();
                var response = await http.GetAsync(link.URL, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                tempFile = Path.Combine(Path.GetTempPath(), $"openlist-archive-{Guid.NewGuid():N}");
                using (var fs2 = File.Create(tempFile))
                    await response.Content.CopyToAsync(fs2);
                archiveStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read);
            }
            else
            {
                return null;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".zip" => ReadZipEntries(archiveStream),
                _ => null // Unsupported archive format
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            archiveStream?.Dispose();
            if (tempFile != null && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    private static List<ArchiveEntryInfo> ReadZipEntries(Stream stream)
    {
        var entries = new List<ArchiveEntryInfo>();
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            entries.Add(new ArchiveEntryInfo
            {
                Name = entry.FullName,
                Size = entry.Length,
                IsDir = entry.FullName.EndsWith('/'),
                IsEncrypted = entry.CompressedLength == 0 && entry.Length > 0 // heuristic for encrypted entries
            });
        }
        return entries;
    }
}

public class ArchiveEntryInfo
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public bool IsEncrypted { get; set; }
}
