using System.Security.Claims;
using Mapster;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static partial class FsEndpoints
{
    internal static void MapFsUploadApi(RouteGroupBuilder group)
    {
        // PUT /fs/put - stream upload (Go: FsStream, uses File-Path header)
        // Note: Go's FsStream does NOT check NoUpload (only FsForm does)
        group.MapPut("put", async (IFileSystemService fs, IUserService users, IMetaService metaSvc, ISettingService settings, HttpContext ctx) =>
        {
            // Go: path from File-Path header, URL-decoded
            var filePath = ctx.Request.Headers["File-Path"].FirstOrDefault() ?? "";
            filePath = Uri.UnescapeDataString(filePath);
            if (string.IsNullOrEmpty(filePath))
                return Results.Json(Resp<object>.Error(400, "File-Path header is required"));

            // Go: user.JoinPath
            var userId = FsHelpers.GetUserId(ctx.User);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            filePath = FsHelpers.JoinPath(u, filePath);

            // Go: FsUp middleware - CanWriteContent || CanWriteContentBypassUserPerms
            var parentPath = filePath.LastIndexOf('/') > 0 ? filePath[..filePath.LastIndexOf('/')] : "/";
            var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
            var parentWriteBypass = parentMeta?.Write == true && FsHelpers.MetaCoversPath(parentMeta.Path, parentPath, parentMeta.WSub);
            if ((u == null || !u.HasPermission(UserPermission.CanWriteContent)) && !parentWriteBypass)
                return Results.Json(Resp<object>.Error(403, "permission denied"));
            // Go: FsUp middleware - CanWrite check on parent
            if (!FsHelpers.CanWrite(u, parentMeta, parentPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: shouldIgnoreSystemFile - only when ignore_system_files setting is enabled
            var putFileName = System.IO.Path.GetFileName(filePath);
            if (settings.GetBool("ignore_system_files") && FsHelpers.IsSystemFile(putFileName))
                return Results.Json(Resp<object>.Error(403, "system file ignored"));

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

        // PUT /fs/form - form upload (Go: FsForm, uses File-Path header, single 'file' field)
        group.MapPut("form", async (IFileSystemService fs, IStorageService storageService, IUserService users, IMetaService metaSvc, ISettingService settings, HttpContext ctx) =>
        {
            // Go: path from File-Path header
            var filePath = ctx.Request.Headers["File-Path"].FirstOrDefault() ?? "";
            filePath = Uri.UnescapeDataString(filePath);
            if (string.IsNullOrEmpty(filePath))
                return Results.Json(Resp<object>.Error(400, "File-Path header is required"));

            // Go: user.JoinPath
            var userId = FsHelpers.GetUserId(ctx.User);
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            filePath = FsHelpers.JoinPath(u, filePath);

            // Go: FsUp middleware - CanWriteContent || CanWriteContentBypassUserPerms
            var parentPath = filePath.LastIndexOf('/') > 0 ? filePath[..filePath.LastIndexOf('/')] : "/";
            var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
            var parentWriteBypass = parentMeta?.Write == true && FsHelpers.MetaCoversPath(parentMeta.Path, parentPath, parentMeta.WSub);
            if ((u == null || !u.HasPermission(UserPermission.CanWriteContent)) && !parentWriteBypass)
                return Results.Json(Resp<object>.Error(403, "permission denied"));
            // Go: FsUp middleware - CanWrite check on parent
            if (!FsHelpers.CanWrite(u, parentMeta, parentPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: overwrite check (default true)
            var overwrite = ctx.Request.Headers["Overwrite"].FirstOrDefault() != "false";
            if (!overwrite)
            {
                var existing = await fs.GetAsync(filePath);
                if (existing != null)
                    return Results.Json(Resp<object>.Error(403, "file exists"));
            }

            // Go: check NoUpload (FsForm checks this, FsStream does not)
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

            var dir = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "/";
            var form = await ctx.Request.ReadFormAsync();

            // Go: c.FormFile("file") - single file field named "file"
            var file = form.Files.GetFile("file");
            if (file == null)
                return Results.Json(Resp<object>.Error(400, "file not found in form"));

            // Go: shouldIgnoreSystemFile - only when ignore_system_files setting is enabled
            if (settings.GetBool("ignore_system_files") && FsHelpers.IsSystemFile(file.FileName))
                return Results.Json(Resp<object>.Error(403, "system file ignored"));

            // Go: last-modified from header (millisecond timestamp)
            var lastModified = DateTime.UtcNow;
            var lmStr = ctx.Request.Headers["Last-Modified"].FirstOrDefault();
            if (!string.IsNullOrEmpty(lmStr) && long.TryParse(lmStr, out var lmMs))
                lastModified = DateTime.UnixEpoch.AddMilliseconds(lmMs);

            var obj = new ObjItem { Name = file.FileName, Size = file.Length, Modified = lastModified };
            var fileStream = new FileStreamer
            {
                Stream = file.OpenReadStream(),
                Obj = obj,
                Size = file.Length
            };
            TaskInfo? createdTask = null;
            if (asTask)
            {
                createdTask = await fs.PutAsTaskAsync(dir, fileStream);
            }
            else
            {
                await fs.PutAsync(dir, fileStream, _ => { });
            }
            if (asTask && createdTask != null)
                return Results.Json(new Resp<object>(new { task = createdTask }));
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        // fs/link - admin only (Go: middlewares.AuthAdmin)
        group.MapPost("link", async (IFileSystemService fs, IStorageService storageService, ISignService sign, HttpContext ctx, [FromBody] LinkReq req) =>
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
                    var proxyUrl = $"{apiUrl}/p{FsHelpers.EncodePath(rawPath)}?d&sign={signStr}";
                    return Results.Json(new Resp<LinkResp>(new LinkResp { URL = proxyUrl }));
                }
            }
            var link = await fs.LinkAsync(rawPath, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "", Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()), Redirect = true });
            if (link == null) return Results.Json(Resp<LinkResp>.Error(500, "failed to get link"));
            return Results.Json(new Resp<LinkResp>(link.Adapt<LinkResp>()));
        }).RequireAuthorization(policy => policy.RequireRole("2"));

        // Direct upload info
        group.MapPost("get_direct_upload_info", async (IFileSystemService fs, IStorageService storageService, IUserService users, HttpContext ctx, [FromBody] DirectUploadReq req) =>
        {
            // Go: url.PathUnescape + user.JoinPath
            var dirPath = Uri.UnescapeDataString(req.Path ?? "/");
            var userId = FsHelpers.GetUserId(ctx.User);
            if (userId.HasValue)
            {
                var u = await users.GetUserByIdAsync(userId.Value);
                dirPath = FsHelpers.JoinPath(u, dirPath);
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
    }
}
