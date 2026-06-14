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
    internal static void MapFsListApi(RouteGroupBuilder group)
    {
        // fs/list - supports both GET and POST, allows guest (Go: Auth(true))
        group.MapMethods("list", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IUserService users, ISignService signSvc, IMetaService metaSvc, ISharingService sharings, ISettingService settings, HttpContext ctx, [FromBody] ListReq req) =>
        {
            // Support sharing path: /@s prefix routes to sharing logic
            if (req.Path.StartsWith("/@s", StringComparison.Ordinal))
            {
                var sharingPath = req.Path[3..]; // strip /@s
                var (sid, subPath) = FsHelpers.SplitSharingPath(sharingPath);
                if (string.IsNullOrEmpty(sid))
                    return Results.Json(Resp<ListResp>.Error(400, "invalid share id"));

                var (sharing, sharingObjs) = await sharings.ListSharingObjectsAsync(sid, subPath, req.Password, req.Refresh);
                if (sharing == null) return Results.Json(Resp<ListResp>.Error(404, "share not found"));
                if (!sharing.Valid()) return Results.Json(Resp<ListResp>.Error(403, "share expired or disabled"));
                if (!sharing.Verify(req.Password)) return Results.Json(Resp<ListResp>.Error(403, "wrong password"));

                // Go: countAccess - increment accessed count per unique IP
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                await sharings.IncrementAccessAsync(sid, ip);

                var sAllItems = sharingObjs.Select(FsHelpers.MapObj).ToList();
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

            // Go: Auth(true) - resolve user (allow anonymous/Guest)
            var (u, userId, resolveError) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: true);
            if (resolveError != null) return resolveError;

            var reqPath = FsHelpers.JoinPath(u, req.Path);

            // Go: common.CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<ListResp>.Error(403, "password is incorrect or you have no permission"));
            // isEncrypt: IsStorageSignEnabled || (meta has password and covers path)
            var listStorage = await fs.GetStorageAsync(reqPath);
            var listEncrypt = (listStorage?.EnableSign == true) ||
                (meta != null && !string.IsNullOrEmpty(meta.Password) && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.PSub));
            // Validate pagination (Go: PageReq.Validate)
            if (req.Page < 1) req.Page = 1;
            if (req.PerPage < 1) req.PerPage = int.MaxValue;

            // Go: canWriteContentAtPath = CanWrite && (CanWriteContent || CanWriteContentBypassUserPerms)
            var canWrite = true;
            var writeContentBypass = false;
            if (meta != null)
            {
                if (userId.HasValue && meta.WriteUsers.Count > 0 && !meta.WriteUsers.Contains(userId.Value) && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.WriteUsersSub))
                    canWrite = false;
                writeContentBypass = meta.Write && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.WSub);
            }
            var canWriteContentAtPath = canWrite && (u == null || u.HasPermission(UserPermission.CanWriteContent) || writeContentBypass);
            if (req.Refresh && !canWriteContentAtPath)
                return Results.Json(Resp<ListResp>.Error(403, "Refresh without permission"));

            // Go: WithStorageDetails only when !user.IsGuest() && !HideStorageDetails
            var hideDetails = settings.GetBool("hide_storage_details");
            var shouldIncludeDetails = req.WithStorageDetails && (u == null || !u.IsGuest) && !hideDetails;
            var args = new ListArgs { ReqPath = reqPath, Refresh = req.Refresh, WithStorageDetails = shouldIncludeDetails };
            var objs = await fs.ListAsync(reqPath, args);

            // Go: whetherHide + filterReadableObjs - filter by hide patterns and per-object read permissions
            var applyHide = FsHelpers.WhetherHide(u, meta, reqPath);
            objs = await FsHelpers.FilterReadableObjsAsync(objs, u, reqPath, meta, applyHide, metaSvc);

            // Go: parent-level mount details only used as fallback (per-object details come from virtual files)
            StorageDetails? parentMountDetails = null;
            var storage = await fs.GetStorageAsync(reqPath);
            if (shouldIncludeDetails && storage != null)
            {
                if (storage.MountDetails != null)
                    parentMountDetails = storage.MountDetails;
                else
                {
                    var detailsDriver = storageService.GetDriverByMountPath(storage.MountPath);
                    if (detailsDriver is IDriverWithDetails withDetails)
                        parentMountDetails = await withDetails.GetDetailsAsync();
                }
            }

            var signAll = settings.GetBool("sign_all");
            var allItems = objs.Select(o => FsHelpers.MapObjWithSign(o, reqPath, listEncrypt, signAll, signSvc, parentMountDetails)).ToList();
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
        }).AllowAnonymous();

        // fs/get - supports both GET and POST, allows guest (Go: Auth(true))
        group.MapMethods("get", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IUserService users, IMetaService metaSvc, ISignService sign, ISharingService sharings, ISettingService settings, HttpContext ctx, [FromBody] GetReq req) =>
        {
            // Support sharing path: /@s prefix routes to sharing logic
            if (req.Path.StartsWith("/@s", StringComparison.Ordinal))
            {
                var sharingPath = req.Path[3..]; // strip /@s
                var (sid, subPath) = FsHelpers.SplitSharingPath(sharingPath);
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

                var sResp = FsHelpers.MapGetResp(sObj);
                if (!sObj.IsDir())
                {
                    var fakePath = $"/{sid}/{subPath}".TrimEnd('/');
                    var apiUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                    sResp.RawURL = $"{apiUrl}/sd{FsHelpers.EncodePath(fakePath)}";
                    if (!string.IsNullOrEmpty(sharing.Pwd)) sResp.RawURL += $"?pwd={sharing.Pwd}";
                }
                sResp.Readme = sharing.Readme;
                sResp.Header = sharing.Header;
                sResp.Provider = "unknown";
                return Results.Json(new Resp<GetResp>(sResp));
            }

            // Go: Auth(true) - resolve user (allow anonymous/Guest)
            var (u, userId, resolveError) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: true);
            if (resolveError != null) return resolveError;

            var reqPath = FsHelpers.JoinPath(u, req.Path);

            // Go: common.CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<GetResp>.Error(403, "password is incorrect or you have no permission"));

            var obj = await fs.GetAsync(reqPath);
            if (obj == null) return Results.Json(Resp<GetResp>.Error(404, "object not found"));
            var resp = FsHelpers.MapGetResp(obj);

            // Get storage info for sign/proxy logic
            var storageForProxy = await fs.GetStorageAsync(reqPath);

            // Add sign (Go: common.Sign(obj, parentPath, isEncrypt(meta, reqPath)))
            // isEncrypt: IsStorageSignEnabled || (meta has password and covers path)
            var storageSignEnabled = storageForProxy?.EnableSign == true;
            var metaEncrypt = meta != null && !string.IsNullOrEmpty(meta.Password) && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.PSub);
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
                    var rawURL = FsHelpers.GenerateDownProxyURL(storageForProxy, reqPath, sign);
                    if (string.IsNullOrEmpty(rawURL))
                    {
                        var query = "";
                        if (encrypt || settings.GetBool("sign_all"))
                            query = "?sign=" + sign.Sign(reqPath);
                        rawURL = $"{ctx.Request.Scheme}://{ctx.Request.Host}/p{FsHelpers.EncodePath(reqPath)}{query}";
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
                        (parentMeta != null && !string.IsNullOrEmpty(parentMeta.Password) && FsHelpers.MetaCoversPath(parentMeta.Path, parentPath, parentMeta.PSub));
                    resp.Related = siblingObjs
                        .Where(o => o.GetName() != obj.GetName() && o.GetName().StartsWith(nameWithoutExt))
                        .Select(o => FsHelpers.MapObjWithSign(o, parentPath, relatedEncrypt, signAll, sign, null)).ToList();
                }
            }

            return Results.Json(new Resp<GetResp>(resp));
        }).AllowAnonymous();
    }
}
