using Mapster;
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
    internal static void MapFsArchiveApi(RouteGroupBuilder group)
    {
        var archiveGroup = group.MapGroup("archive");

        // archive/meta - archive metadata (Go: FsArchiveMetaSplit in fsAndShare with Auth(true))
        archiveGroup.MapMethods("meta", ["GET", "POST"], async (
            IFileSystemService fs, IUserService users, IMetaService metaSvc,
            ISignService signSvc, ISettingService settings, ISharingService sharingSvc,
            HttpContext ctx, [FromBody] ArchiveMetaReq req) =>
        {
            // Go: /@s prefix → sharing archive meta
            if (req.Path.StartsWith("/@s"))
                return await HandleSharingArchiveMeta(sharingSvc, fs, settings, ctx, req);

            var (user, _, error) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (error != null) return error;

            // Go: user.CanReadArchives() - permission check
            if (user != null && !user.HasPermission(UserPermission.CanReadArchives))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var reqPath = FsHelpers.JoinPath(user, req.Path);

            // Go: meta check + CanAccess
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanAccess(user, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<object>.Error(403, "password is incorrect or you have no permission"));

            // Build archive args (Go: model.ArchiveMetaArgs)
            var archiveArgs = new ArchiveArgs
            {
                Password = req.ArchivePass ?? "",
                LinkArgs = new LinkArgs
                {
                    Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                    Type = ctx.Request.Query["type"].ToString()
                }
            };

            ArchiveMetaResult? result;
            try
            {
                result = await fs.GetArchiveMetaAsync(reqPath, archiveArgs, ctx.RequestAborted);
            }
            catch (WrongArchivePasswordException ex)
            {
                return Results.Json(Resp<object>.Error(202, ex.Message));
            }

            if (result == null)
                return Results.Json(Resp<object>.Error(500, "failed to read archive meta"));

            // Go: sign if meta is encrypted or SignAll is enabled
            var sign = "";
            var isEncrypt = meta != null && !string.IsNullOrEmpty(meta.Password)
                && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.PSub);
            var signAll = settings.GetBool("sign_all");
            if (isEncrypt || signAll)
                sign = signSvc.SignArchive(reqPath);

            // Go: RawURL = GetApiUrl(c) + api + EncodePath(reqPath, true)
            var api = result.DriverProviding ? "/ad" : "/ae";
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var rawUrl = $"{baseUrl}{api}{FsHelpers.EncodePath(reqPath)}";

            // Convert ArchiveContentEntry tree to ArchiveContentResp tree
            var content = result.Content.Adapt<List<ArchiveContentResp>>();

            return Results.Json(new Resp<ArchiveMetaResp>(new ArchiveMetaResp
            {
                Comment = result.Comment,
                IsEncrypted = result.IsEncrypted,
                Content = content,
                RawURL = rawUrl,
                Sign = sign
            }));
        }).WithSummary("获取压缩包元数据");

        // archive/list - archive content listing (Go: FsArchiveListSplit in fsAndShare with Auth(true))
        archiveGroup.MapMethods("list", ["GET", "POST"], async (
            IFileSystemService fs, IUserService users, IMetaService metaSvc,
            ISharingService sharingSvc, ISettingService settings,
            HttpContext ctx, [FromBody] ArchiveListReq req) =>
        {
            // Go: /@s prefix → sharing archive list
            if (req.Path.StartsWith("/@s"))
                return await HandleSharingArchiveList(sharingSvc, fs, settings, ctx, req);

            var (user, _, error) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (error != null) return error;

            if (user != null && !user.HasPermission(UserPermission.CanReadArchives))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var reqPath = FsHelpers.JoinPath(user, req.Path);

            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanAccess(user, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<object>.Error(403, "password is incorrect or you have no permission"));

            // Build list args (Go: model.ArchiveListArgs)
            var listArgs = new ArchiveListArgs
            {
                ArchiveInnerArgs = new ArchiveInnerArgs
                {
                    Password = req.ArchivePass ?? "",
                    InnerPath = req.InnerPath ?? "",
                    LinkArgs = new LinkArgs
                    {
                        Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                        Type = ctx.Request.Query["type"].ToString()
                    }
                },
                Refresh = req.Refresh,
                Page = req.Page,
                PerPage = req.PerPage
            };

            List<IObj> items;
            int total;
            try
            {
                (items, total) = await fs.ListArchiveAsync(reqPath, listArgs, ctx.RequestAborted);
            }
            catch (WrongArchivePasswordException ex)
            {
                return Results.Json(Resp<object>.Error(202, ex.Message));
            }

            if (items == null)
                return Results.Json(Resp<object>.Error(500, "failed to list archive"));

            // Go: toObjsRespWithoutSignAndThumb for each item
            var content = items.Select(FsHelpers.MapObj).ToList();

            return Results.Json(new Resp<ArchiveListResp>(new ArchiveListResp
            {
                Content = content,
                Total = total
            }));
        }).WithSummary("列出压缩包内容");

        // archive/decompress - decompress archive to target directory (Go: FsArchiveDecompress in _fs)
        archiveGroup.MapPost("decompress", async (
            IFileSystemService fs, IUserService users, IMetaService metaSvc,
            HttpContext ctx, [FromBody] ArchiveDecompressReq req) =>
        {
            var (user, userId, error) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (error != null) return error;

            // Go: user.CanDecompress()
            if (user != null && !user.HasPermission(UserPermission.CanDecompress))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var names = req.Names ?? [];
            if (names.Count == 0)
                return Results.Json(Resp<object>.Error(400, "no files specified"));

            // Go: build srcPaths with user.JoinPath
            var srcPaths = names.Select(n => FsHelpers.JoinPath(user, req.SrcDir.TrimEnd('/') + "/" + n)).ToList();
            var dstDir = FsHelpers.JoinPath(user, req.DstDir);

            // Go: write permission on destination
            var dstMeta = await metaSvc.GetNearestMetaAsync(dstDir);
            if (!FsHelpers.CanWrite(user, dstMeta, dstDir))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: create decompress task per source file
            var createdTasks = new List<TaskInfo>();
            foreach (var srcPath in srcPaths)
            {
                var decompressArgs = new ArchiveDecompressArgs
                {
                    Password = req.ArchivePass ?? "",
                    InnerPath = req.InnerPath ?? "",
                    CacheFull = req.CacheFull,
                    PutIntoNewDir = req.PutIntoNewDir,
                    Overwrite = req.Overwrite,
                    LinkArgs = new LinkArgs
                    {
                        Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                        Type = ctx.Request.Query["type"].ToString()
                    }
                };

                try
                {
                    var task = await fs.ArchiveDecompressAsync(srcPath, dstDir, decompressArgs, ctx.RequestAborted);
                    if (task == null)
                        return Results.Json(Resp<object>.Error(500, $"driver does not support decompression for: {srcPath}"));
                    createdTasks.Add(task);
                }
                catch (WrongArchivePasswordException ex)
                {
                    return Results.Json(Resp<object>.Error(202, ex.Message));
                }
            }

            return Results.Json(new Resp<object>(new { task = createdTasks }));
        }).RequireAuthorization().WithSummary("解压压缩包到目标目录");
    }

    // MapContentTree is now handled by Mapster configuration in MappingConfig.cs

    /// <summary>Handle sharing archive meta (/@s prefix). Go: SharingArchiveMeta in sharing.go</summary>
    private static async Task<IResult> HandleSharingArchiveMeta(
        ISharingService sharingSvc, IFileSystemService fs, ISettingService settings,
        HttpContext ctx, ArchiveMetaReq req)
    {
        if (!settings.GetBool("share_archive_preview"))
            return Results.Json(Resp<object>.Error(403, "sharing archives previewing is not allowed"));

        // Parse: /@s/{sid}/{path...} → strip /@s → /{sid}/{path}
        var stripped = req.Path[3..].TrimStart('/'); // after /@s
        var slashIdx = stripped.IndexOf('/');
        var sid = slashIdx >= 0 ? stripped[..slashIdx] : stripped;
        var subPath = slashIdx >= 0 ? stripped[(slashIdx + 1)..] : "";

        if (string.IsNullOrEmpty(sid))
            return Results.Json(Resp<object>.Error(400, "invalid share id"));

        var sharing = await sharingSvc.GetSharingAsync(sid);
        if (sharing == null)
            return Results.Json(Resp<object>.Error(404, "sharing not found"));
        if (!sharing.Valid())
            return Results.Json(Resp<object>.Error(403, "sharing is invalid or expired"));
        if (!sharing.Verify(req.Password))
            return Results.Json(Resp<object>.Error(403, "password is incorrect"));

        // Resolve archive path: sharing.Files[0] + subPath
        var files = sharing.Files ?? [];
        var basePath = files.Count == 1 ? files[0] : "";
        var fullPath = string.IsNullOrEmpty(subPath) ? basePath : $"{basePath.TrimEnd('/')}/{subPath}";
        fullPath = "/" + fullPath.TrimStart('/');

        // Increment access count
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
        await sharingSvc.IncrementAccessAsync(sid, ip);

        // Get archive meta
        var archiveArgs = new ArchiveArgs
        {
            Password = req.ArchivePass ?? "",
            LinkArgs = new LinkArgs
            {
                Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                Type = ctx.Request.Query["type"].ToString()
            }
        };

        ArchiveMetaResult? result;
        try
        {
            result = await fs.GetArchiveMetaAsync(fullPath, archiveArgs, ctx.RequestAborted);
        }
        catch (WrongArchivePasswordException ex)
        {
            return Results.Json(Resp<object>.Error(202, ex.Message));
        }

        if (result == null)
            return Results.Json(Resp<object>.Error(500, "failed to read archive meta"));

        // Build RawURL pointing to /sad/{sid}/{path}
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var fakePath = $"/{sid}/{subPath}";
        var rawUrl = $"{baseUrl}/sad{FsHelpers.EncodePath(fakePath)}";
        if (!string.IsNullOrEmpty(sharing.Pwd))
            rawUrl += $"?pwd={sharing.Pwd}";

        var content = result.Content.Adapt<List<ArchiveContentResp>>();

        return Results.Json(new Resp<ArchiveMetaResp>(new ArchiveMetaResp
        {
            Comment = result.Comment,
            IsEncrypted = result.IsEncrypted,
            Content = content,
            RawURL = rawUrl,
            Sign = ""
        }));
    }

    /// <summary>Handle sharing archive list (/@s prefix). Go: SharingArchiveList in sharing.go</summary>
    private static async Task<IResult> HandleSharingArchiveList(
        ISharingService sharingSvc, IFileSystemService fs, ISettingService settings,
        HttpContext ctx, ArchiveListReq req)
    {
        if (!settings.GetBool("share_archive_preview"))
            return Results.Json(Resp<object>.Error(403, "sharing archives previewing is not allowed"));

        // Parse: /@s/{sid}/{path...}
        var stripped = req.Path[3..].TrimStart('/');
        var slashIdx = stripped.IndexOf('/');
        var sid = slashIdx >= 0 ? stripped[..slashIdx] : stripped;
        var subPath = slashIdx >= 0 ? stripped[(slashIdx + 1)..] : "";

        if (string.IsNullOrEmpty(sid))
            return Results.Json(Resp<object>.Error(400, "invalid share id"));

        var sharing = await sharingSvc.GetSharingAsync(sid);
        if (sharing == null)
            return Results.Json(Resp<object>.Error(404, "sharing not found"));
        if (!sharing.Valid())
            return Results.Json(Resp<object>.Error(403, "sharing is invalid or expired"));
        if (!sharing.Verify(req.Password))
            return Results.Json(Resp<object>.Error(403, "password is incorrect"));

        // Resolve archive path
        var files = sharing.Files ?? [];
        var basePath = files.Count == 1 ? files[0] : "";
        var fullPath = string.IsNullOrEmpty(subPath) ? basePath : $"{basePath.TrimEnd('/')}/{subPath}";
        fullPath = "/" + fullPath.TrimStart('/');

        // Increment access count
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
        await sharingSvc.IncrementAccessAsync(sid, ip);

        // List archive contents
        var listArgs = new ArchiveListArgs
        {
            ArchiveInnerArgs = new ArchiveInnerArgs
            {
                Password = req.ArchivePass ?? "",
                InnerPath = req.InnerPath ?? "",
                LinkArgs = new LinkArgs
                {
                    Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                    Type = ctx.Request.Query["type"].ToString()
                }
            },
            Refresh = req.Refresh,
            Page = req.Page,
            PerPage = req.PerPage
        };

        List<IObj> items;
        int total;
        try
        {
            (items, total) = await fs.ListArchiveAsync(fullPath, listArgs, ctx.RequestAborted);
        }
        catch (WrongArchivePasswordException ex)
        {
            return Results.Json(Resp<object>.Error(202, ex.Message));
        }

        if (items == null)
            return Results.Json(Resp<object>.Error(500, "failed to list archive"));

        var content = items.Select(FsHelpers.MapObj).ToList();

        return Results.Json(new Resp<ArchiveListResp>(new ArchiveListResp
        {
            Content = content,
            Total = total
        }));
    }
}
