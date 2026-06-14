using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static partial class FsEndpoints
{
    internal static void MapFsWriteApi(RouteGroupBuilder group)
    {
        group.MapPost("mkdir", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] MkdirReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            var reqPath = FsHelpers.JoinPath(u, req.Path);

            var parentPath = reqPath.LastIndexOf('/') > 0 ? reqPath[..reqPath.LastIndexOf('/')] : "/";
            var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
            var parentWriteBypass = parentMeta?.Write == true && FsHelpers.MetaCoversPath(parentMeta.Path, parentPath, parentMeta.WSub);
            if (!u.HasPermission(UserPermission.CanWriteContent) && !parentWriteBypass)
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            if (!FsHelpers.CanWrite(u, parentMeta, parentPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            await fs.MakeDirAsync(reqPath);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("rename", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] RenameReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRename))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var reqPath = FsHelpers.JoinPath(u, req.Path);

            if (string.IsNullOrEmpty(req.Name) || req.Name == "." || req.Name == ".." ||
                req.Name.Contains('/') || req.Name.Contains('\\'))
                return Results.Json(Resp<object>.Error(403, "invalid name"));

            var parentPath = reqPath.LastIndexOf('/') > 0 ? reqPath[..reqPath.LastIndexOf('/')] : "/";
            var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
            if (!FsHelpers.CanWrite(u, parentMeta, parentPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

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

        group.MapPost("batch_rename", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] BatchRenameReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRename))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var reqPath = FsHelpers.JoinPath(u, req.SrcDir);

            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            foreach (var item in req.RenameObjects ?? [])
            {
                if (string.IsNullOrEmpty(item.SrcPath) || string.IsNullOrEmpty(item.NewName))
                    continue;
                if (item.NewName == "." || item.NewName == ".." ||
                    item.NewName.Contains('/') || item.NewName.Contains('\\'))
                    return Results.Json(Resp<object>.Error(403, "invalid name"));
                var filePath = $"{reqPath}/{item.SrcPath}";
                await fs.RenameAsync(filePath, item.NewName);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("regex_rename", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] RegexRenameReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRename))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var reqPath = FsHelpers.JoinPath(u, req.SrcDir);

            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanWrite(u, meta, reqPath))
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
                    if (string.IsNullOrEmpty(newName) || newName == "." || newName == ".." ||
                        newName.Contains('/') || newName.Contains('\\'))
                        return Results.Json(Resp<object>.Error(403, "invalid name"));
                    await fs.RenameAsync($"{reqPath}/{name}", newName);
                }
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("move", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] MoveReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanMove))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var srcDir = FsHelpers.JoinPath(u, req.SrcDir);
            var dstDir = FsHelpers.JoinPath(u, req.DstDir);

            var srcMeta = await metaSvc.GetNearestMetaAsync(srcDir);
            if (!FsHelpers.CanWrite(u, srcMeta, srcDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var dstMeta = await metaSvc.GetNearestMetaAsync(dstDir);
            if (!FsHelpers.CanWrite(u, dstMeta, dstDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            if (req.Names == null || req.Names.Count == 0)
                return Results.Json(Resp<object>.Error(400, "empty file names"));

            if (!srcDir.EndsWith('/')) srcDir += '/';
            foreach (var name in req.Names)
            {
                var srcPath = srcDir.TrimEnd('/') + "/" + name;
                if (!srcPath.StartsWith(srcDir))
                    continue;

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

        group.MapPost("recursive_move", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] RecursiveMoveReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanMove))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var srcDir = FsHelpers.JoinPath(u, req.SrcDir);
            var dstDir = FsHelpers.JoinPath(u, req.DstDir);

            var srcMeta = await metaSvc.GetNearestMetaAsync(srcDir);
            if (!FsHelpers.CanWrite(u, srcMeta, srcDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));
            var dstMeta = await metaSvc.GetNearestMetaAsync(dstDir);
            if (!FsHelpers.CanWrite(u, dstMeta, dstDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var conflictPolicy = req.ConflictPolicy ?? "overwrite";
            var rootFiles = await fs.ListAsync(srcDir);

            var existingFileNames = new HashSet<string>();
            if (conflictPolicy != "overwrite")
            {
                var dstFiles = await fs.ListAsync(dstDir);
                foreach (var f in dstFiles)
                    existingFileNames.Add(f.GetName());
            }

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
                    var subFiles = await fs.ListAsync(movingFileName);
                    foreach (var subFile in subFiles)
                        queue.Enqueue((subFile, movingFileName));
                }
                else
                {
                    if (movingFilePath == dstDir)
                        continue;

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

        group.MapPost("copy", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] CopyReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanCopy))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var srcDir = FsHelpers.JoinPath(u, req.SrcDir);
            var dstDir = FsHelpers.JoinPath(u, req.DstDir);

            var srcMeta = await metaSvc.GetNearestMetaAsync(srcDir);
            if (!FsHelpers.CanRead(u, srcMeta, srcDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var dstMeta = await metaSvc.GetNearestMetaAsync(dstDir);
            if (!FsHelpers.CanWrite(u, dstMeta, dstDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            if (req.Names == null || req.Names.Count == 0)
                return Results.Json(Resp<object>.Error(400, "empty file names"));

            if (!srcDir.EndsWith('/')) srcDir += '/';
            foreach (var name in req.Names)
            {
                var srcPath = srcDir.TrimEnd('/') + "/" + name;
                if (!srcPath.StartsWith(srcDir))
                    continue;

                var baseName = srcPath[(srcPath.LastIndexOf('/') + 1)..];
                if (baseName == "." || baseName == "/")
                    return Results.Json(Resp<object>.Error(400, $"invalid file name [{name}]"));

                if (!req.Overwrite)
                {
                    var dstPath = dstDir.TrimEnd('/') + "/" + name;
                    var dstObj = await fs.GetAsync(dstPath);
                    if (dstObj != null)
                    {
                        if (!req.SkipExisting && !req.Merge)
                            return Results.Json(Resp<object>.Error(403, $"file [{name}] exists"));
                        if (!req.Merge || !dstObj.IsDir())
                            continue;
                    }
                }
                if (req.Merge)
                    await fs.MergeAsync(srcPath, dstDir);
                else
                    await fs.CopyAsync(srcPath, dstDir);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("remove", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] RemoveReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRemove))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var reqPath = FsHelpers.JoinPath(u, req.Dir);

            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            if (req.Names == null || req.Names.Count == 0)
                return Results.Json(Resp<object>.Error(400, "empty file names"));

            if (!reqPath.EndsWith('/')) reqPath += '/';
            foreach (var name in req.Names)
            {
                var fullPath = reqPath.TrimEnd('/') + "/" + name;
                if (!fullPath.StartsWith(reqPath))
                    continue;
                await fs.RemoveAsync(fullPath);
            }
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("remove_empty_directory", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, [FromBody] RemoveEmptyDirReq req) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanRemove))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var reqPath = FsHelpers.JoinPath(u, req.SrcDir);

            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

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
                    await fs.RemoveAsync(removingFilePath);
                    removedFiles.Add(removingFilePath);
                    if (fileParentMap.TryGetValue(removingFile, out var parentFile))
                        removingFiles.Enqueue(parentFile);
                }
                else
                {
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
    }
}
