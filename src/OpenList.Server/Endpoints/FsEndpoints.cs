using System.IO.Compression;
using System.Linq;
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
        group.MapMethods("list", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IUserService users, ISignService signSvc, IMetaService metaSvc, ISharingService sharings, ISettingService settings, HttpContext ctx, ClaimsPrincipal user, ListReq req) =>
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

                // Go: countAccess - increment accessed count per unique IP
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                await sharings.IncrementAccessAsync(sid, ip);

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
                    Write = false,
                    Provider = "unknown"
                }));
            }

            // Go: user.JoinPath
            var userId = GetUserId(user);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;

            // Go: user.IsGuest() && user.Disabled → 401
            if (u != null && u.IsGuest && u.Disabled)
                return Results.Json(Resp<ListResp>.Error(401, "Guest user is disabled, login please"));

            var reqPath = JoinPath(u, req.Path);

            // Go: common.CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<ListResp>.Error(403, "password is incorrect or you have no permission"));
            // isEncrypt: IsStorageSignEnabled || (meta has password and covers path)
            var listStorage = await fs.GetStorageAsync(reqPath);
            var listEncrypt = (listStorage?.EnableSign == true) ||
                (meta != null && !string.IsNullOrEmpty(meta.Password) && MetaCoversPath(meta.Path, reqPath, meta.PSub));
            // Validate pagination (Go: PageReq.Validate)
            if (req.Page < 1) req.Page = 1;
            if (req.PerPage < 1) req.PerPage = int.MaxValue;

            // Go: canWriteContentAtPath = CanWrite && (CanWriteContent || CanWriteContentBypassUserPerms)
            var canWrite = true;
            var writeContentBypass = false;
            if (meta != null)
            {
                if (userId.HasValue && meta.WriteUsers.Count > 0 && !meta.WriteUsers.Contains(userId.Value) && MetaCoversPath(meta.Path, reqPath, meta.WriteUsersSub))
                    canWrite = false;
                writeContentBypass = meta.Write && MetaCoversPath(meta.Path, reqPath, meta.WSub);
            }
            var canWriteContentAtPath = canWrite && (u == null || u.HasPermission(UserPermission.CanWriteContent) || writeContentBypass);
            if (req.Refresh && !canWriteContentAtPath)
                return Results.Json(Resp<ListResp>.Error(403, "Refresh without permission"));

            // Go: WithStorageDetails only when !user.IsGuest() && !HideStorageDetails
            var hideDetails = settings.GetBool("hide_storage_details");
            var shouldIncludeDetails = req.WithStorageDetails && (u == null || !u.IsGuest) && !hideDetails;
            var args = new ListArgs { ReqPath = reqPath, Refresh = req.Refresh, WithStorageDetails = shouldIncludeDetails };
            var objs = await fs.ListAsync(reqPath, args);

            // Get storage details if requested (Go: model.GetStorageDetails)
            StorageDetails? mountDetails = null;
            var storage = await fs.GetStorageAsync(reqPath);
            if (req.WithStorageDetails && storage != null)
            {
                if (storage.MountDetails != null)
                    mountDetails = storage.MountDetails;
                else
                {
                    var detailsDriver = storageService.GetDriverByMountPath(storage.MountPath);
                    if (detailsDriver is IDriverWithDetails withDetails)
                        mountDetails = await withDetails.GetDetailsAsync();
                }
            }

            var signAll = settings.GetBool("sign_all");
            var allItems = objs.Select(o => MapObjWithSign(o, reqPath, listEncrypt, signAll, signSvc, mountDetails)).ToList();
            var total = allItems.Count;

            // Apply pagination (Go: pagination function)
            var start = (req.Page - 1) * req.PerPage;
            var paged = start >= total ? [] : allItems.Skip(start).Take(req.PerPage).ToList();

            // Add meta info to response (Go: readme, header, write from meta)
            var resp = new ListResp { Content = paged, Total = total, Write = canWrite, WriteContentBypass = writeContentBypass };
            if (meta != null)
            {
                if (meta.Readme != null && (meta.Path == reqPath || meta.RSub)) resp.Readme = meta.Readme;
                if (meta.Header != null && (meta.Path == reqPath || meta.HeaderSub)) resp.Header = meta.Header;
            }

            // Go: provider is always "unknown" in FsList, directUploadTools only when canWriteContentAtPath
            resp.Provider = "unknown";
            if (canWriteContentAtPath && storage != null)
            {
                var driver = storageService.GetDriverByMountPath(storage.MountPath);
                if (driver is IDriverDirectUploader directUploader)
                    resp.DirectUploadTools = directUploader.GetDirectUploadTools();
            }

            return Results.Json(new Resp<ListResp>(resp));
        }).RequireAuthorization();

        // fs/get - supports both GET and POST, allows guest (Go: Auth(true))
        group.MapMethods("get", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IUserService users, IMetaService metaSvc, ISignService sign, ISharingService sharings, ISettingService settings, HttpContext ctx, ClaimsPrincipal userPrincipal, GetReq req) =>
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

                // Go: countAccess - increment accessed count per unique IP
                var sIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                await sharings.IncrementAccessAsync(sid, sIp);

                var sResp = MapGetResp(sObj);
                if (!sObj.IsDir())
                {
                    var fakePath = $"/{sid}/{subPath}".TrimEnd('/');
                    var apiUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                    sResp.RawURL = $"{apiUrl}/sd{EncodePath(fakePath)}";
                    if (!string.IsNullOrEmpty(sharing.Pwd)) sResp.RawURL += $"?pwd={sharing.Pwd}";
                }
                sResp.Readme = sharing.Readme;
                sResp.Header = sharing.Header;
                sResp.Provider = "unknown";
                return Results.Json(new Resp<GetResp>(sResp));
            }

            // Go: user.JoinPath
            var userId = GetUserId(userPrincipal);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;

            // Go: user.IsGuest() && user.Disabled → 401
            if (u != null && u.IsGuest && u.Disabled)
                return Results.Json(Resp<GetResp>.Error(401, "Guest user is disabled, login please"));

            var reqPath = JoinPath(u, req.Path);

            // Go: common.CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<GetResp>.Error(403, "password is incorrect or you have no permission"));

            var obj = await fs.GetAsync(reqPath);
            if (obj == null) return Results.Json(Resp<GetResp>.Error(404, "object not found"));
            var resp = MapGetResp(obj);

            // Get storage info for sign/proxy logic
            var storageForProxy = await fs.GetStorageAsync(reqPath);

            // Add sign (Go: common.Sign(obj, parentPath, isEncrypt(meta, reqPath)))
            // isEncrypt: IsStorageSignEnabled || (meta has password and covers path)
            var storageSignEnabled = storageForProxy?.EnableSign == true;
            var metaEncrypt = meta != null && !string.IsNullOrEmpty(meta.Password) && MetaCoversPath(meta.Path, reqPath, meta.PSub);
            var encrypt = storageSignEnabled || metaEncrypt;
            var signAll = settings.GetBool("sign_all");
            if (!obj.IsDir() && (encrypt || signAll))
            {
                // Go: common.Sign(obj, parentPath, ...) - parentPath is Dir(reqPath)
                var signPath = reqPath.TrimEnd('/').LastIndexOf('/') is var idx && idx > 0 ? reqPath[..idx] : "/";
                resp.Sign = sign.Sign(signPath);
            }

            if (!obj.IsDir())
            {
                // Go: MustProxy or WebProxy → use proxy URL
                var driverForProxy = storageForProxy != null ? storageService.GetDriverByMountPath(storageForProxy.MountPath) : null;
                var mustProxy = driverForProxy != null && driverForProxy.Config().MustProxy();
                var webProxy = storageForProxy?.WebProxy == true;

                if (mustProxy || webProxy)
                {
                    // Go: GenerateDownProxyURL - use storage.DownProxyURL if available
                    var rawURL = GenerateDownProxyURL(storageForProxy, reqPath, sign);
                    if (string.IsNullOrEmpty(rawURL))
                    {
                        var query = "";
                        if (encrypt || settings.GetBool("sign_all"))
                            query = "?sign=" + sign.Sign(reqPath);
                        rawURL = $"{ctx.Request.Scheme}://{ctx.Request.Host}/p{EncodePath(reqPath)}{query}";
                    }
                    resp.RawURL = rawURL;
                }
                else
                {
                    var link = await fs.LinkAsync(reqPath, new LinkArgs
                    {
                        IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                        Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                        Redirect = true
                    });
                    if (link != null)
                    {
                        resp.RawURL = link.URL;
                    }
                }
            }
            // Add meta info (Go: readme, header from meta)
            if (meta != null)
            {
                if (meta.Readme != null && (meta.Path == reqPath || meta.RSub)) resp.Readme = meta.Readme;
                if (meta.Header != null && (meta.Path == reqPath || meta.HeaderSub)) resp.Header = meta.Header;
            }
            // Add provider (Go: GetProvider(obj) || storage.Config().Name)
            var storage = await fs.GetStorageAsync(reqPath);
            var getDriver = storage != null ? storageService.GetDriverByMountPath(storage.MountPath) : null;
            resp.Provider = getDriver != null ? getDriver.Config().Name : (storage?.Driver ?? "unknown");

            // Add related files (Go: filterRelated)
            if (!obj.IsDir())
            {
                var parentPath = reqPath.TrimEnd('/').LastIndexOf('/') is var idx && idx > 0 ? reqPath[..idx] : "/";
                var siblingObjs = await fs.ListAsync(parentPath, new ListArgs { ReqPath = parentPath });
                if (siblingObjs != null)
                {
                    var nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(obj.GetName());
                    // Go: isEncrypt(parentMeta, parentPath) for Related sign
                    var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
                    var relatedEncrypt = (storage?.EnableSign == true) ||
                        (parentMeta != null && !string.IsNullOrEmpty(parentMeta.Password) && MetaCoversPath(parentMeta.Path, parentPath, parentMeta.PSub));
                    resp.Related = siblingObjs
                        .Where(o => o.GetName() != obj.GetName() && o.GetName().StartsWith(nameWithoutExt))
                        .Select(o => MapObjWithSign(o, parentPath, relatedEncrypt, signAll, sign, null)).ToList();
                }
            }

            return Results.Json(new Resp<GetResp>(resp));
        }).RequireAuthorization();

        // Archive endpoints (allows guest, Go: Auth(true))
        // Write operations (requires auth, not guest)
        group.MapPost("mkdir", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, MkdirReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            // Go: user.JoinPath
            var reqPath = JoinPath(u, req.Path);

            // Go: CanWriteContent check on parent path (CanWriteContent || CanWriteContentBypassUserPerms)
            var parentPath = reqPath.LastIndexOf('/') > 0 ? reqPath[..reqPath.LastIndexOf('/')] : "/";
            var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
            var parentWriteBypass = parentMeta?.Write == true && MetaCoversPath(parentMeta.Path, parentPath, parentMeta.WSub);
            if (!u.HasPermission(UserPermission.CanWriteContent) && !parentWriteBypass)
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: CanWrite check on parent path
            if (!CanWrite(u, parentMeta, parentPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            await fs.MakeDirAsync(reqPath);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("rename", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, RenameReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRename))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath
            var reqPath = JoinPath(u, req.Path);

            // Go: checkRelativePath - name cannot contain / or \, cannot be empty, "." or ".."
            if (string.IsNullOrEmpty(req.Name) || req.Name == "." || req.Name == ".." ||
                req.Name.Contains('/') || req.Name.Contains('\\'))
                return Results.Json(Resp<object>.Error(403, "invalid name"));

            // Go: CanWrite check on parent path
            var parentPath = reqPath.LastIndexOf('/') > 0 ? reqPath[..reqPath.LastIndexOf('/')] : "/";
            var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
            if (!CanWrite(u, parentMeta, parentPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: if !req.Overwrite, check if destination exists
            if (!req.Overwrite)
            {
                var dstPath = parentPath + "/" + req.Name;
                if (dstPath != reqPath)
                {
                    var dstObj = await fs.GetAsync(dstPath);
                    if (dstObj != null)
                        return Results.Json(Resp<object>.Error(403, $"file [{req.Name}] exists"));
                }
            }

            await fs.RenameAsync(reqPath, req.Name);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("batch_rename", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, BatchRenameReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRename))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath
            var reqPath = JoinPath(u, req.SrcDir);

            // Go: CanWrite check
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            foreach (var item in req.RenameObjects ?? [])
            {
                if (string.IsNullOrEmpty(item.SrcPath) || string.IsNullOrEmpty(item.NewName))
                    continue;
                // Go: checkRelativePath
                if (item.NewName == "." || item.NewName == ".." ||
                    item.NewName.Contains('/') || item.NewName.Contains('\\'))
                    return Results.Json(Resp<object>.Error(403, "invalid name"));
                var filePath = $"{reqPath}/{item.SrcPath}";
                await fs.RenameAsync(filePath, item.NewName);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("regex_rename", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, RegexRenameReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRename))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath
            var reqPath = JoinPath(u, req.SrcDir);

            // Go: CanWrite check
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            System.Text.RegularExpressions.Regex? srcRegexp;
            try { srcRegexp = new System.Text.RegularExpressions.Regex(req.Regex); }
            catch { return Results.Json(Resp<object>.Error(500, "invalid regex")); }

            var objs = await fs.ListAsync(reqPath);
            foreach (var obj in objs)
            {
                var name = obj.GetName();
                if (srcRegexp.IsMatch(name))
                {
                    var newName = srcRegexp.Replace(name, req.Replace);
                    // Go: checkRelativePath
                    if (string.IsNullOrEmpty(newName) || newName == "." || newName == ".." ||
                        newName.Contains('/') || newName.Contains('\\'))
                        return Results.Json(Resp<object>.Error(403, "invalid name"));
                    await fs.RenameAsync($"{reqPath}/{name}", newName);
                }
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("move", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, MoveReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanMove))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath for srcDir and dstDir
            var srcDir = JoinPath(u, req.SrcDir);
            var dstDir = JoinPath(u, req.DstDir);

            // Go: CanWrite check on srcDir
            var srcMeta = await metaSvc.GetNearestMetaAsync(srcDir);
            if (!CanWrite(u, srcMeta, srcDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: CanWrite check on dstDir
            var dstMeta = await metaSvc.GetNearestMetaAsync(dstDir);
            if (!CanWrite(u, dstMeta, dstDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            if (req.Names == null || req.Names.Count == 0)
                return Results.Json(Resp<object>.Error(400, "empty file names"));

            // Go: path safety check and overwrite/skip_existing logic
            if (!srcDir.EndsWith('/')) srcDir += '/';
            foreach (var name in req.Names)
            {
                var srcPath = srcDir.TrimEnd('/') + "/" + name;
                if (!srcPath.StartsWith(srcDir))
                    continue; // skip relative path traversal

                // Go: validate base name
                var baseName = srcPath[(srcPath.LastIndexOf('/') + 1)..];
                if (baseName == "." || baseName == "/")
                    return Results.Json(Resp<object>.Error(400, $"invalid file name [{name}]"));

                if (!req.Overwrite)
                {
                    var dstPath = dstDir.TrimEnd('/') + "/" + name;
                    var dstObj = await fs.GetAsync(dstPath);
                    if (dstObj != null)
                    {
                        if (req.SkipExisting) continue;
                        return Results.Json(Resp<object>.Error(403, $"file [{name}] exists"));
                    }
                }
                await fs.MoveAsync(srcPath, dstDir);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("recursive_move", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, RecursiveMoveReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanMove))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath
            var srcDir = JoinPath(u, req.SrcDir);
            var dstDir = JoinPath(u, req.DstDir);

            // Go: CanWrite check on srcDir and dstDir
            var srcMeta = await metaSvc.GetNearestMetaAsync(srcDir);
            if (!CanWrite(u, srcMeta, srcDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));
            var dstMeta = await metaSvc.GetNearestMetaAsync(dstDir);
            if (!CanWrite(u, dstMeta, dstDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: recursive move with conflict policy
            var conflictPolicy = req.ConflictPolicy ?? "overwrite";
            var rootFiles = await fs.ListAsync(srcDir);

            // Build list of existing file names in dstDir if not overwrite
            var existingFileNames = new HashSet<string>();
            if (conflictPolicy != "overwrite")
            {
                var dstFiles = await fs.ListAsync(dstDir);
                foreach (var f in dstFiles)
                    existingFileNames.Add(f.GetName());
            }

            // BFS traversal: collect all files to move
            var movingFileNames = new List<string>();
            var queue = new Queue<(IObj obj, string path)>();
            foreach (var f in rootFiles)
                queue.Enqueue((f, srcDir));

            while (queue.Count > 0)
            {
                var (movingFile, movingFilePath) = queue.Dequeue();
                var movingFileName = $"{movingFilePath}/{movingFile.GetName()}";

                if (movingFile.IsDir())
                {
                    // Directory: enqueue children for recursive traversal
                    var subFiles = await fs.ListAsync(movingFileName);
                    foreach (var subFile in subFiles)
                        queue.Enqueue((subFile, movingFileName));
                }
                else
                {
                    if (movingFilePath == dstDir)
                        continue; // same directory, don't move

                    if (existingFileNames.Contains(movingFile.GetName()))
                    {
                        if (conflictPolicy == "cancel")
                            return Results.Json(Resp<object>.Error(403, $"file [{movingFile.GetName()}] exists"));
                        else if (conflictPolicy == "skip")
                            continue;
                    }
                    else if (conflictPolicy != "overwrite")
                    {
                        existingFileNames.Add(movingFile.GetName());
                    }
                    movingFileNames.Add(movingFileName);
                }
            }

            var count = 0;
            foreach (var fileName in movingFileNames)
            {
                await fs.MoveAsync(fileName, dstDir);
                count++;
            }

            return Results.Json(new Resp<string>($"Successfully moved {count} {(count == 1 ? "file" : "files")}"));
        }).RequireAuthorization();

        group.MapPost("copy", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, CopyReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanCopy))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath for srcDir and dstDir
            var srcDir = JoinPath(u, req.SrcDir);
            var dstDir = JoinPath(u, req.DstDir);

            // Go: CanRead check on srcDir (copy only needs read access to source)
            var srcMeta = await metaSvc.GetNearestMetaAsync(srcDir);
            if (!CanRead(u, srcMeta, srcDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: CanWrite check on dstDir
            var dstMeta = await metaSvc.GetNearestMetaAsync(dstDir);
            if (!CanWrite(u, dstMeta, dstDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            if (req.Names == null || req.Names.Count == 0)
                return Results.Json(Resp<object>.Error(400, "empty file names"));

            // Go: path safety check and overwrite/skip_existing logic
            if (!srcDir.EndsWith('/')) srcDir += '/';
            foreach (var name in req.Names)
            {
                var srcPath = srcDir.TrimEnd('/') + "/" + name;
                if (!srcPath.StartsWith(srcDir))
                    continue; // skip relative path traversal

                // Go: validate base name
                var baseName = srcPath[(srcPath.LastIndexOf('/') + 1)..];
                if (baseName == "." || baseName == "/")
                    return Results.Json(Resp<object>.Error(400, $"invalid file name [{name}]"));

                if (!req.Overwrite)
                {
                    var dstPath = dstDir.TrimEnd('/') + "/" + name;
                    var dstObj = await fs.GetAsync(dstPath);
                    if (dstObj != null)
                    {
                        // Go: if SkipExisting or (Merge and not dir), skip
                        if (!req.SkipExisting && !req.Merge)
                            return Results.Json(Resp<object>.Error(403, $"file [{name}] exists"));
                        if (!req.Merge || !dstObj.IsDir())
                            continue;
                    }
                }
                // Go: Merge mode uses fs.Merge, otherwise fs.Copy
                if (req.Merge)
                    await fs.MergeAsync(srcPath, dstDir);
                else
                    await fs.CopyAsync(srcPath, dstDir);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("remove", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, RemoveReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRemove))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath
            var reqPath = JoinPath(u, req.Dir);

            // Go: CanWrite check
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            if (req.Names == null || req.Names.Count == 0)
                return Results.Json(Resp<object>.Error(400, "empty file names"));

            // Go: path safety check
            if (!reqPath.EndsWith('/')) reqPath += '/';
            foreach (var name in req.Names)
            {
                var fullPath = reqPath.TrimEnd('/') + "/" + name;
                if (!fullPath.StartsWith(reqPath))
                    continue; // skip relative path traversal
                await fs.RemoveAsync(fullPath);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("remove_empty_directory", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, RemoveEmptyDirReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRemove))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath
            var reqPath = JoinPath(u, req.SrcDir);

            // Go: CanWrite check
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: recursive empty directory removal with parent recheck
            var filePathMap = new Dictionary<IObj, string>();
            var fileParentMap = new Dictionary<IObj, IObj>();
            var removingFiles = new Queue<IObj>();
            var removedFiles = new HashSet<string>();

            var rootObjs = await fs.ListAsync(reqPath);
            foreach (var obj in rootObjs)
            {
                if (!obj.IsDir()) continue;
                removingFiles.Enqueue(obj);
                filePathMap[obj] = reqPath;
            }

            while (removingFiles.Count > 0)
            {
                var removingFile = removingFiles.Dequeue();
                var removingFilePath = $"{filePathMap[removingFile]}/{removingFile.GetName()}";

                if (removedFiles.Contains(removingFilePath))
                    continue;

                var subFiles = await fs.ListAsync(removingFilePath);
                if (subFiles.Count == 0)
                {
                    // remove empty directory
                    await fs.RemoveAsync(removingFilePath);
                    removedFiles.Add(removingFilePath);
                    // recheck parent folder
                    if (fileParentMap.TryGetValue(removingFile, out var parentFile))
                        removingFiles.Enqueue(parentFile);
                }
                else
                {
                    // recursive: enqueue sub-directories
                    foreach (var subFile in subFiles)
                    {
                        if (!subFile.IsDir()) continue;
                        removingFiles.Enqueue(subFile);
                        filePathMap[subFile] = removingFilePath;
                        fileParentMap[subFile] = removingFile;
                    }
                }
            }

            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        // PUT /fs/put - stream upload (Go: FsStream, uses File-Path header)
        group.MapPut("put", async (IFileSystemService fs, IStorageService storageService, IUserService users, IMetaService metaSvc, HttpContext ctx) =>
        {
            // Go: path from File-Path header, URL-decoded
            var filePath = ctx.Request.Headers["File-Path"].FirstOrDefault() ?? "";
            filePath = Uri.UnescapeDataString(filePath);
            if (string.IsNullOrEmpty(filePath))
                return Results.Json(Resp<object>.Error(400, "File-Path header is required"));

            // Go: user.JoinPath
            var userId = GetUserId(ctx.User);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            filePath = JoinPath(u, filePath);

            // Go: FsUp middleware - CanWriteContent || CanWriteContentBypassUserPerms
            var parentPath = filePath.LastIndexOf('/') > 0 ? filePath[..filePath.LastIndexOf('/')] : "/";
            var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
            var parentWriteBypass = parentMeta?.Write == true && MetaCoversPath(parentMeta.Path, parentPath, parentMeta.WSub);
            if ((u == null || !u.HasPermission(UserPermission.CanWriteContent)) && !parentWriteBypass)
                return Results.Json(Resp<object>.Error(403, "permission denied"));
            // Go: FsUp middleware - CanWrite check on parent
            if (!CanWrite(u, parentMeta, parentPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: shouldIgnoreSystemFile
            var putFileName = System.IO.Path.GetFileName(filePath);
            if (IsSystemFile(putFileName))
                return Results.Json(Resp<object>.Error(403, "system file ignored"));

            // Go: overwrite check (default true)
            var overwrite = ctx.Request.Headers["Overwrite"].FirstOrDefault() != "false";
            if (!overwrite)
            {
                var existing = await fs.GetAsync(filePath);
                if (existing != null)
                    return Results.Json(Resp<object>.Error(403, "file exists"));
            }

            // Go: check NoUpload
            var putStorage = await fs.GetStorageAsync(filePath);
            if (putStorage != null)
            {
                var putDriver = storageService.GetDriverByMountPath(putStorage.MountPath);
                if (putDriver != null && putDriver.Config().NoUpload)
                    return Results.Json(Resp<object>.Error(405, "current storage doesn't support upload"));
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

            // Go: As-Task header (default false)
            var asTask = ctx.Request.Headers["As-Task"].FirstOrDefault() == "true";

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
                if (asTask)
                {
                    var task = await fs.PutAsTaskAsync(dir, fileStream);
                    if (task != null)
                        return Results.Json(new Resp<object>(new { task }));
                }
                else
                {
                    await fs.PutAsync(dir, fileStream, _ => { });
                }
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        // PUT /fs/form - form upload (Go: FsForm, uses File-Path header)
        group.MapPut("form", async (IFileSystemService fs, IStorageService storageService, IUserService users, IMetaService metaSvc, HttpContext ctx) =>
        {
            // Go: path from File-Path header
            var filePath = ctx.Request.Headers["File-Path"].FirstOrDefault() ?? "";
            filePath = Uri.UnescapeDataString(filePath);
            if (string.IsNullOrEmpty(filePath))
                return Results.Json(Resp<object>.Error(400, "File-Path header is required"));

            // Go: user.JoinPath
            var userId = GetUserId(ctx.User);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            filePath = JoinPath(u, filePath);

            // Go: FsUp middleware - CanWriteContent || CanWriteContentBypassUserPerms
            var parentPath = filePath.LastIndexOf('/') > 0 ? filePath[..filePath.LastIndexOf('/')] : "/";
            var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
            var parentWriteBypass = parentMeta?.Write == true && MetaCoversPath(parentMeta.Path, parentPath, parentMeta.WSub);
            if ((u == null || !u.HasPermission(UserPermission.CanWriteContent)) && !parentWriteBypass)
                return Results.Json(Resp<object>.Error(403, "permission denied"));
            // Go: FsUp middleware - CanWrite check on parent
            if (!CanWrite(u, parentMeta, parentPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: shouldIgnoreSystemFile
            var formFileName = System.IO.Path.GetFileName(filePath);
            if (IsSystemFile(formFileName))
                return Results.Json(Resp<object>.Error(403, "system file ignored"));

            var overwrite = ctx.Request.Headers["Overwrite"].FirstOrDefault() != "false";
            if (!overwrite)
            {
                var existing = await fs.GetAsync(filePath);
                if (existing != null)
                    return Results.Json(Resp<object>.Error(403, "file exists"));
            }

            // Go: check NoUpload
            var formStorage = await fs.GetStorageAsync(filePath);
            if (formStorage != null)
            {
                var formDriver = storageService.GetDriverByMountPath(formStorage.MountPath);
                if (formDriver != null && formDriver.Config().NoUpload)
                    return Results.Json(Resp<object>.Error(405, "current storage doesn't support upload"));
            }

            if (!ctx.Request.HasFormContentType)
                return Results.Json(Resp<object>.Error(400, "expected multipart form data"));

            // Go: As-Task header (default false)
            var asTask = ctx.Request.Headers["As-Task"].FirstOrDefault() == "true";
            TaskInfo? createdTask = null;

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
                if (asTask)
                {
                    createdTask = await fs.PutAsTaskAsync(dir, fileStream);
                }
                else
                {
                    await fs.PutAsync(dir, fileStream, _ => { });
                }
            }
            if (asTask && createdTask != null)
                return Results.Json(new Resp<object>(new { task = createdTask }));
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        // fs/link - admin only (Go: middlewares.AuthAdmin)
        group.MapPost("link", async (IFileSystemService fs, IStorageService storageService, ISignService sign, HttpContext ctx, LinkReq req) =>
        {
            // Go: Link is admin-only, path is full path (no JoinPath needed)
            var rawPath = req.Path;

            // Go: check NoLinkURL - return proxy URL instead
            var linkStorage = await fs.GetStorageAsync(rawPath);
            if (linkStorage != null)
            {
                var linkDriver = storageService.GetDriverByMountPath(linkStorage.MountPath);
                if (linkDriver != null && linkDriver.Config().NoLinkURL)
                {
                    var signStr = sign.Sign(rawPath);
                    var apiUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                    var proxyUrl = $"{apiUrl}/p{EncodePath(rawPath)}?d&sign={signStr}";
                    return Results.Json(new Resp<LinkResp>(new LinkResp { URL = proxyUrl }));
                }
            }
            var link = await fs.LinkAsync(rawPath, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "", Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()), Redirect = true });
            if (link == null) return Results.Json(Resp<LinkResp>.Error(500, "failed to get link"));
            return Results.Json(new Resp<LinkResp>(new LinkResp { URL = link.URL, Header = link.Header, Concurrency = link.Concurrency, PartSize = link.PartSize, ContentLength = link.ContentLength }));
        }).RequireAuthorization(policy => policy.RequireRole("2"));

        // fs/search - requires auth + search index enabled
        group.MapMethods("search", ["GET", "POST"], async (ISearchService search, IConfiguration config, IUserService users, IMetaService metaSvc, HttpContext ctx, ClaimsPrincipal userPrincipal, SearchReq req) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });

            // Go: user.JoinPath(req.Parent)
            var userId = GetUserId(userPrincipal);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            req.Parent = JoinPath(u, req.Parent);

            var results = await search.SearchAsync(req.Parent, req.Keywords, req.Page, req.PerPage);
            // Go: Total is the count before filtering
            var total = results.Count;
            // Scope filter: 0=all, 1=dir only, 2=file only
            if (req.Scope > 0)
                results = results.Where(r => req.Scope == 1 ? r.IsDir : !r.IsDir).ToList();

            // Go: filter by BasePath and CanAccess
            var filtered = new List<Core.Interfaces.SearchResult>();
            foreach (var r in results)
            {
                if (u != null && !string.IsNullOrEmpty(u.BasePath) && u.BasePath != "/" &&
                    !r.Parent.StartsWith(u.BasePath, StringComparison.Ordinal))
                    continue;
                var rMeta = await metaSvc.GetNearestMetaAsync(r.Parent);
                // Go: CanAccess(user, meta, path.Join(node.Parent, node.Name), req.Password)
                var fullPath = r.Parent.TrimEnd('/') + "/" + r.Name;
                if (!CanAccess(u, rMeta, fullPath, null, ctx))
                    continue;
                filtered.Add(r);
            }

            return Results.Json(new Resp<SearchResp>(new SearchResp { Content = filtered, Total = total }));
        }).RequireAuthorization();

        // fs/dirs - requires auth
        group.MapMethods("dirs", ["GET", "POST"], async (IFileSystemService fs, IMetaService metaSvc, IUserService users, HttpContext ctx, DirsReq req) =>
        {
            // Go: ForceRoot check - admin only
            var userId = GetUserId(ctx.User);
            var isAdmin = ctx.User.IsInRole("2");
            if (req.ForceRoot && !isAdmin)
                return Results.Json(Resp<object>.Error(403, "Permission denied"));

            var reqPath = req.Path;
            if (!req.ForceRoot && userId.HasValue)
            {
                var u = await users.GetUserByIdAsync(userId.Value);
                if (u != null && !string.IsNullOrEmpty(u.BasePath) && u.BasePath != "/")
                    reqPath = u.BasePath.TrimEnd('/') + "/" + req.Path.TrimStart('/');
            }

            // Go: CanAccess - checks Hide, CanRead, and password
            var dirsUser = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanAccess(dirsUser, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<object>.Error(403, "password is incorrect or you have no permission"));

            var objs = await fs.ListAsync(reqPath);
            var dirs = objs.Where(o => o.IsDir()).Select(o => new DirResp { Name = o.GetName(), Modified = o.GetModTime() }).ToList();
            return Results.Json(new Resp<List<DirResp>>(dirs));
        }).RequireAuthorization();

        // fs/other - requires auth
        group.MapMethods("other", ["GET", "POST"], async (IFileSystemService fs, IUserService users, IMetaService metaSvc, HttpContext ctx, ClaimsPrincipal userPrincipal, OtherReq req) =>
        {
            // Go: user.JoinPath
            var userId = GetUserId(userPrincipal);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            var reqPath = JoinPath(u, req.Path);

            // Go: common.CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<object>.Error(403, "password is incorrect or you have no permission"));

            var obj = await fs.GetAsync(reqPath);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "object not found"));
            var result = await fs.OtherAsync(new OtherArgs { Obj = obj, Method = req.Method, Data = req.Data });
            return Results.Json(new Resp<object>(result));
        }).RequireAuthorization();

        group.MapPost("add_offline_download", async (OfflineDownloadService downloadService, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, AddOfflineDownloadReq req, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanAddOfflineDownloadTasks))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath(req.Path)
            var reqPath = JoinPath(u, req.Path);

            // Go: CanWrite check
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            try
            {
                var tasks = await downloadService.AddUrlsAsync(req.Urls ?? [], reqPath, req.Tool, ct);
                return Results.Json(new Resp<object>(new { tasks }));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(Resp<object>.Error(400, ex.Message));
            }
        }).RequireAuthorization();

        // Archive endpoints (Go: /api/fs/archive/meta, /api/fs/archive/list)
        group.MapMethods("archive/meta", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IUserService users, IMetaService metaSvc, HttpContext ctx, ClaimsPrincipal userPrincipal, ArchiveMetaReq req) =>
        {
            var userId = GetUserId(userPrincipal);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            var reqPath = JoinPath(u, req.Path);

            // Go: CanReadArchives permission check
            if (u != null && !u.HasPermission(UserPermission.CanReadArchives))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<object>.Error(403, "password is incorrect or you have no permission"));

            var obj = await fs.GetAsync(reqPath);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "object not found"));

            var storage = await fs.GetStorageAsync(reqPath);
            if (storage == null) return Results.Json(Resp<object>.Error(500, "storage not found"));

            var driver = storageService.GetDriverByMountPath(storage.MountPath);
            if (driver is not IDriverArchiveReader archiveReader)
                return Results.Json(Resp<object>.Error(500, "driver does not support archive reading"));

            var archiveArgs = new ArchiveArgs { Password = req.ArchivePass };
            var archiveMeta = await archiveReader.GetArchiveMetaAsync(obj, archiveArgs);
            return Results.Json(new Resp<ArchiveMeta>(archiveMeta));
        }).RequireAuthorization();

        group.MapMethods("archive/list", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IUserService users, IMetaService metaSvc, HttpContext ctx, ClaimsPrincipal userPrincipal, ArchiveListReq req) =>
        {
            var userId = GetUserId(userPrincipal);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            var reqPath = JoinPath(u, req.Path);

            if (u != null && !u.HasPermission(UserPermission.CanReadArchives))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<object>.Error(403, "password is incorrect or you have no permission"));

            var obj = await fs.GetAsync(reqPath);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "object not found"));

            var storage = await fs.GetStorageAsync(reqPath);
            if (storage == null) return Results.Json(Resp<object>.Error(500, "storage not found"));

            var driver = storageService.GetDriverByMountPath(storage.MountPath);
            if (driver is not IDriverArchiveReader archiveReader)
                return Results.Json(Resp<object>.Error(500, "driver does not support archive reading"));

            var archiveArgs = new ArchiveInnerArgs { Password = req.ArchivePass, InnerPath = req.InnerPath };
            var entries = await archiveReader.ListArchiveAsync(obj, archiveArgs);
            // Go: pagination - return PageResp{Content, Total}
            var total = entries.Count;
            var start = (req.Page - 1) * req.PerPage;
            if (start > total) start = total;
            var end = start + req.PerPage;
            if (end > total) end = total;
            var pageEntries = entries.GetRange(start, end - start);
            return Results.Json(new Resp<PageResp<List<IObj>>>(new PageResp<List<IObj>> { Content = pageEntries, Total = total }));
        }).RequireAuthorization();

        group.MapPost("archive/decompress", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ITaskService taskService, ClaimsPrincipal userPrincipal, ArchiveDecompressReq req) =>
        {
            // Go: user.CanDecompress() permission check
            var userId = GetUserId(userPrincipal);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));
            if (!u.HasPermission(UserPermission.CanDecompress))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.JoinPath for srcDir and dstDir
            var srcDir = JoinPath(u, req.SrcDir);
            var dstDir = JoinPath(u, req.DstDir);

            // Go: CanWrite check on dstDir
            var dstMeta = await metaSvc.GetNearestMetaAsync(dstDir);
            if (!CanWrite(u, dstMeta, dstDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var names = req.Names ?? [];
            var task = await taskService.CreateTaskAsync("decompress", $"Decompress {srcDir}/{string.Join(",", names)}", async (taskId, ct) =>
            {
                foreach (var name in names)
                {
                    var srcPath = string.IsNullOrEmpty(srcDir) || srcDir == "/"
                        ? $"/{name}"
                        : $"{srcDir}/{name}";
                    var link = await fs.LinkAsync(srcPath, new LinkArgs());
                    if (link == null) continue;

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
                        else continue;

                        var ext = Path.GetExtension(name).ToLowerInvariant();

                        if (ext == ".zip")
                        {
                            using var archive = new System.IO.Compression.ZipArchive(archiveStream, System.IO.Compression.ZipArchiveMode.Read);
                            var extractDir = req.PutIntoNewDir
                                ? Path.Combine(dstDir, Path.GetFileNameWithoutExtension(name))
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
                }
            });
            return Results.Json(new Resp<object>(new { task = new[] { task } }));
        }).RequireAuthorization();

        // Torrent endpoints
        group.MapPost("torrent/parse", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/upload_parse", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/rapid_upload", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/generate", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();

        // Direct upload info
        group.MapPost("get_direct_upload_info", async (IFileSystemService fs, IStorageService storageService, IUserService users, HttpContext ctx, DirectUploadReq req) =>
        {
            // Go: url.PathUnescape + user.JoinPath
            var dirPath = Uri.UnescapeDataString(req.Path ?? "/");
            var userId = GetUserId(ctx.User);
            if (userId.HasValue)
            {
                var u = await users.GetUserByIdAsync(userId.Value);
                dirPath = JoinPath(u, dirPath);
            }

            // Go: overwrite check - check if file already exists
            var overwrite = ctx.Request.Headers["Overwrite"].FirstOrDefault() != "false";
            if (!overwrite && !string.IsNullOrEmpty(req.FileName))
            {
                var filePath = dirPath.TrimEnd('/') + "/" + req.FileName;
                var existing = await fs.GetAsync(filePath);
                if (existing != null)
                    return Results.Json(Resp<object>.Error(403, "file exists"));
            }

            var dirObj = await fs.GetAsync(dirPath);
            if (dirObj == null || !dirObj.IsDir())
                return Results.Json(Resp<object>.Error(404, "directory not found"));

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

            return Results.Json(Resp<object>.Error(500, "driver does not support direct upload"));
        }).RequireAuthorization();

        return endpoints;
    }

    private static Dictionary<string, string>? ParseHashInfo(string? hashStr)
    {
        if (string.IsNullOrEmpty(hashStr) || hashStr == "{}") return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(hashStr); }
        catch { return null; }
    }

    private static ObjResp MapObj(IObj obj) => new()
    {
        Name = obj.GetName(),
        Size = obj.GetSize(),
        IsDir = obj.IsDir(),
        Modified = obj.GetModTime(),
        Created = obj.GetCreateTime(),
        HashInfoStr = obj.GetHash(),
        HashInfo = ParseHashInfo(obj.GetHash()),
        Type = ObjTypeHelper.GetObjType(obj.GetName(), obj.IsDir()),
        Thumb = ObjTypeHelper.GetThumb(obj),
    };

    private static ObjResp MapObjWithSign(IObj obj, string parent, bool encrypt, bool signAll, ISignService signSvc, StorageDetails? mountDetails) => new()
    {
        Name = obj.GetName(),
        Size = obj.GetSize(),
        IsDir = obj.IsDir(),
        Modified = obj.GetModTime(),
        Created = obj.GetCreateTime(),
        HashInfoStr = obj.GetHash(),
        HashInfo = ParseHashInfo(obj.GetHash()),
        Type = ObjTypeHelper.GetObjType(obj.GetName(), obj.IsDir()),
        Thumb = ObjTypeHelper.GetThumb(obj),
        // Go: Sign = obj.IsDir() ? "" : (!encrypt && !signAll) ? "" : sign(path)
        Sign = obj.IsDir() ? null : (encrypt || signAll ? signSvc.Sign(string.Join("/", parent.TrimEnd('/'), obj.GetName())) : null),
        MountDetails = obj.IsDir() ? mountDetails : null,
    };

    private static GetResp MapGetResp(IObj obj) => new()
    {
        Name = obj.GetName(),
        Size = obj.GetSize(),
        IsDir = obj.IsDir(),
        Modified = obj.GetModTime(),
        Created = obj.GetCreateTime(),
        HashInfoStr = obj.GetHash(),
        HashInfo = ParseHashInfo(obj.GetHash()),
        Type = ObjTypeHelper.GetObjType(obj.GetName(), obj.IsDir()),
        Thumb = ObjTypeHelper.GetThumb(obj),
    };

    // Go: EncodePath(path, true) = PathEscape each segment, then join with /
    private static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private static string? GenerateDownProxyURL(Storage? storage, string reqPath, ISignService sign)
    {
        if (storage == null || string.IsNullOrEmpty(storage.DownProxyURL))
            return null;
        var baseURL = storage.DownProxyURL.Split('\n')[0];
        var query = "";
        if (!storage.DisableProxySign)
            query = "?sign=" + sign.Sign(reqPath);
        // Go: EncodePath(reqPath, true) = PathEscape each segment, then join with /
        var encodedPath = EncodePath(reqPath);
        return $"{baseURL}{encodedPath}{query}";
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }

    /// <summary>
    /// Go: user.JoinPath - prepends user.BasePath if set and not "/"
    /// </summary>
    private static string JoinPath(User? user, string path)
    {
        if (user != null && !string.IsNullOrEmpty(user.BasePath) && user.BasePath != "/")
            return user.BasePath.TrimEnd('/') + "/" + path.TrimStart('/');
        return path;
    }

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

    private static bool CanRead(User? user, Meta? meta, string path)
    {
        if (user == null) return true;
        if (meta != null && meta.ReadUsers.Count > 0 && !meta.ReadUsers.Contains(user.Id) && MetaCoversPath(meta.Path, path, meta.ReadUsersSub))
            return false;
        return true;
    }

    // Go: common.CanAccess - checks Hide, CanRead, and password
    private static bool CanAccess(User? user, Meta? meta, string reqPath, string? password, HttpContext ctx)
    {
        // Hide check: if user can't see hides and path matches meta.Hide regex
        if (meta != null && (user == null || !user.HasPermission(UserPermission.CanSeeHides)) && !string.IsNullOrEmpty(meta.Hide) &&
            MetaCoversPath(meta.Path, GetParentPath(reqPath), meta.HSub))
        {
            var fileName = System.IO.Path.GetFileName(reqPath);
            foreach (var hidePattern in meta.Hide.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (Regex.IsMatch(fileName, hidePattern))
                        return false;
                }
                catch { /* invalid regex, skip */ }
            }
        }
        // CanRead check
        if (!CanRead(user, meta, reqPath))
            return false;
        // Password check
        if (user != null && user.HasPermission(UserPermission.CanAccessWithoutPassword))
            return true;
        if (meta == null || string.IsNullOrEmpty(meta.Password))
            return true;
        if (!MetaCoversPath(meta.Path, reqPath, meta.PSub))
            return true;
        return meta.Password == password;
    }

    private static string GetParentPath(string path)
    {
        var idx = path.TrimEnd('/').LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    // Go: utils.IsSystemFile - checks for common system files
    private static readonly HashSet<string> SystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "desktop.ini", "Thumbs.db", "@eaDir"
    };
    private static bool IsSystemFile(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        if (SystemFiles.Contains(filename)) return true;
        if (filename.StartsWith("._")) return true;
        return false;
    }

    private static bool CanWrite(User? user, Meta? meta, string path)
    {
        if (user == null) return true;
        if (meta != null && meta.WriteUsers.Count > 0 && !meta.WriteUsers.Contains(user.Id) && MetaCoversPath(meta.Path, path, meta.WriteUsersSub))
            return false;
        return true;
    }

    private static (string sid, string path) SplitSharingPath(string sharingPath)
    {
        var trimmed = sharingPath.TrimStart('/');
        var slashIdx = trimmed.IndexOf('/');
        return slashIdx < 0
            ? (trimmed, "")
            : (trimmed[..slashIdx], trimmed[(slashIdx + 1)..]);
    }

    internal static async Task<List<ArchiveEntryInfo>?> ReadArchiveEntriesAsync(IFileSystemService fs, string path, string? password)
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
