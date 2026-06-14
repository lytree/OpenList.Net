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
        // fs/list - supports both GET and POST, allows guest (Go: fsAndShare with Auth(true))
        // Go: FsListSplit → SharingList if /@s prefix, else check guest disabled then FsList
        group.MapMethods("list", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IUserService users, ISignService signSvc, IMetaService metaSvc, ISharingService sharings, ISettingService settings, HttpContext ctx, [FromBody] ListReq req) =>
        {
            // Go: req.Validate()
            if (req.Page < 1) req.Page = 1;
            if (req.PerPage < 1) req.PerPage = int.MaxValue;

            // Go: FsListSplit - Support sharing path: /@s prefix routes to sharing logic
            if (req.Path.StartsWith("/@s", StringComparison.Ordinal))
            {
                // Go: req.Path = strings.TrimPrefix(req.Path, "/@s")
                req.Path = req.Path[3..]; // strip /@s
                // Go: SharingList
                var (sid, subPath) = FsHelpers.SplitSharingPath(req.Path);
                if (string.IsNullOrEmpty(sid))
                    return Results.Json(Resp<ListResp>.Error(400, "invalid share id"));

                var (sharing, sharingObjs) = await sharings.ListSharingObjectsAsync(sid, subPath, req.Password, req.Refresh);
                if (sharing == null) return Results.Json(Resp<ListResp>.Error(404, "share not found"));
                if (!sharing.Valid()) return Results.Json(Resp<ListResp>.Error(403, "share expired or disabled"));
                if (!sharing.Verify(req.Password)) return Results.Json(Resp<ListResp>.Error(403, "wrong password"));

                // Go: countAccess - increment accessed count per unique IP
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                await sharings.IncrementAccessAsync(sid, ip);

                // Go: pagination(objs, &req.PageReq)
                var sTotal = sharingObjs.Count;
                var sStart = (req.Page - 1) * req.PerPage;
                var sPaged = sStart >= sTotal ? [] : sharingObjs.Skip(sStart).Take(req.PerPage).ToList();

                // Go: toObjsResp with empty sign (sharing objects don't get signed)
                var sContent = sPaged.Select(FsHelpers.MapObj).ToList();

                return Results.Json(new Resp<ListResp>(new ListResp
                {
                    Content = sContent,
                    Total = sTotal,
                    Readme = sharing.Readme,
                    Header = sharing.Header,
                    Write = false,
                    Provider = "unknown"
                }));
            }

            // Go: FsListSplit - check if guest user is disabled
            // Go: if user.IsGuest() && user.Disabled { ErrorStrResp(401) }
            var (u, userId, resolveError) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (resolveError != null) return resolveError;

            // Go: FsList(c, req, user)
            // Go: reqPath, err := user.JoinPath(req.Path)
            var reqPath = FsHelpers.JoinPath(u, req.Path);

            // Go: meta, err := op.GetNearestMeta(reqPath)
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);

            // Go: if !common.CanAccess(user, meta, reqPath, req.Password) { ErrorStrResp(403) }
            if (!FsHelpers.CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<ListResp>.Error(403, "password is incorrect or you have no permission"));

            // Go: canWriteContentAtPath := common.CanWrite(user, meta, reqPath) && (user.CanWriteContent() || common.CanWriteContentBypassUserPerms(meta, reqPath))
            var canWrite = FsHelpers.CanWrite(u, meta, reqPath);
            var writeContentBypass = FsHelpers.CanWriteContentBypassUserPerms(meta, reqPath);
            var canWriteContentAtPath = canWrite && (u == null || u.HasPermission(UserPermission.CanWriteContent) || writeContentBypass);
            if (req.Refresh && !canWriteContentAtPath)
                return Results.Json(Resp<ListResp>.Error(403, "Refresh without permission"));

            // Go: fs.List(ctx, reqPath, &fs.ListArgs{Refresh, WithStorageDetails: !user.IsGuest() && !setting.GetBool(conf.HideStorageDetails)})
            // Note: Go ignores req.WithStorageDetails and always computes it from user/settings
            var hideDetails = settings.GetBool("hide_storage_details");
            var withStorageDetails = (u == null || !u.IsGuest) && !hideDetails;
            var args = new ListArgs { ReqPath = reqPath, Refresh = req.Refresh, WithStorageDetails = withStorageDetails };
            List<IObj> objs;
            try
            {
                objs = await fs.ListAsync(reqPath, args);
            }
            catch (Exception ex)
            {
                // Go: if err != nil { common.ErrorResp(c, err, 500) }
                return Results.Json(Resp<ListResp>.Error(500, $"failed list: {ex.Message}"));
            }

            // Go: pagination(objs, &req.PageReq) - done BEFORE toObjsResp in Go
            var total = objs.Count;
            var start = (req.Page - 1) * req.PerPage;
            var pagedObjs = start >= total ? [] : objs.Skip(start).Take(req.PerPage).ToList();

            // Go: isEncrypt(meta, reqPath) = IsStorageSignEnabled(reqPath) || (meta has password && MetaCoversPath)
            var listStorage = await fs.GetStorageAsync(reqPath);
            var listEncrypt = (listStorage?.EnableSign == true) ||
                (meta != null && !string.IsNullOrEmpty(meta.Password) && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.PSub));

            // Go: toObjsResp(objs, reqPath, isEncrypt(meta, reqPath))
            // Go: per-object mount details from ObjStorageDetails wrapper, fallback to parent-level
            StorageDetails? parentMountDetails = null;
            var storage = await fs.GetStorageAsync(reqPath);
            if (withStorageDetails && storage != null)
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
            var content = pagedObjs.Select(o => FsHelpers.MapObjWithSign(o, reqPath, listEncrypt, signAll, signSvc, parentMountDetails)).ToList();

            // Go: directUploadTools - only when canWriteContentAtPath
            List<string>? directUploadTools = null;
            if (canWriteContentAtPath)
            {
                var storageForUpload = await fs.GetStorageAsync(reqPath);
                if (storageForUpload != null)
                {
                    var driver = storageService.GetDriverByMountPath(storageForUpload.MountPath);
                    if (driver is IDriverDirectUploader directUploader)
                        directUploadTools = directUploader.GetDirectUploadTools();
                }
            }

            // Go: FsListResp{Content, Total, Readme, Header, Write, WriteContentBypass, Provider: "unknown", DirectUploadTools}
            var resp = new ListResp
            {
                Content = content,
                Total = total,
                Write = canWrite,
                WriteContentBypass = writeContentBypass,
                Provider = "unknown",
                DirectUploadTools = directUploadTools
            };

            // Go: getReadme(meta, reqPath) - uses MetaCoversPath
            if (meta != null && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.RSub))
                resp.Readme = meta.Readme;
            // Go: getHeader(meta, reqPath) - uses MetaCoversPath
            if (meta != null && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.HeaderSub))
                resp.Header = meta.Header;

            return Results.Json(new Resp<ListResp>(resp));
        }).AllowAnonymous();

        // fs/get - supports both GET and POST, allows guest (Go: fsAndShare with Auth(true))
        // Go: FsGetSplit → SharingGet if /@s prefix, else check guest disabled then FsGet
        group.MapMethods("get", ["GET", "POST"], async (IFileSystemService fs, IStorageService storageService, IUserService users, IMetaService metaSvc, ISignService sign, ISharingService sharings, ISettingService settings, HttpContext ctx, [FromBody] GetReq req) =>
        {
            // Go: FsGetSplit - Support sharing path: /@s prefix routes to sharing logic
            if (req.Path.StartsWith("/@s", StringComparison.Ordinal))
            {
                // Go: req.Path = strings.TrimPrefix(req.Path, "/@s")
                req.Path = req.Path[3..]; // strip /@s
                // Go: SharingGet
                var (sid, subPath) = FsHelpers.SplitSharingPath(req.Path);
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

                // Go: SharingGet builds ObjResp then FsGetResp
                var sResp = FsHelpers.MapGetResp(sObj);
                if (!sObj.IsDir())
                {
                    // Go: rawURL = fmt.Sprintf("%s/sd/%s", apiUrl, sid/path)
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

            // Go: FsGetSplit - check if guest user is disabled
            var (u, userId, resolveError) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (resolveError != null) return resolveError;

            // Go: FsGet(c, req, user)
            // Go: reqPath, err := user.JoinPath(req.Path)
            var reqPath = FsHelpers.JoinPath(u, req.Path);

            // Go: meta, err := op.GetNearestMeta(reqPath)
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);

            // Go: if !common.CanAccess(user, meta, reqPath, req.Password) { ErrorStrResp(403) }
            if (!FsHelpers.CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<GetResp>.Error(403, "password is incorrect or you have no permission"));

            // Go: obj, err := fs.Get(ctx, reqPath, &fs.GetArgs{WithStorageDetails: !user.IsGuest() && !setting.GetBool(conf.HideStorageDetails)})
            var obj = await fs.GetAsync(reqPath);
            if (obj == null) return Results.Json(Resp<GetResp>.Error(404, "object not found"));

            // Go: storage, err := fs.GetStorage(reqPath, &fs.GetStoragesArgs{})
            var storage = await fs.GetStorageAsync(reqPath);
            var getDriver = storage != null ? storageService.GetDriverByMountPath(storage.MountPath) : null;

            // Go: provider, ok := model.GetProvider(obj); if !ok && err == nil { provider = storage.Config().Name }
            var provider = "unknown";
            if (obj is ObjItem objItem && !string.IsNullOrEmpty(objItem.Provider))
                provider = objItem.Provider;
            else if (getDriver != null)
                provider = getDriver.Config().Name;
            else if (storage != null)
                provider = storage.Driver;

            // Go: isEncrypt(meta, reqPath) = IsStorageSignEnabled(reqPath) || (meta has password && MetaCoversPath)
            var storageSignEnabled = storage?.EnableSign == true;
            var metaEncrypt = meta != null && !string.IsNullOrEmpty(meta.Password) && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.PSub);
            var encrypt = storageSignEnabled || metaEncrypt;
            var signAll = settings.GetBool("sign_all");

            // Go: parentPath = stdpath.Dir(reqPath)
            var parentPath = FsHelpers.GetParentPath(reqPath);

            // Go: FsGetResp{ObjResp{...Sign: common.Sign(obj, parentPath, isEncrypt(meta, reqPath))...}, RawURL, Readme, Header, Provider, Related}
            var resp = FsHelpers.MapGetResp(obj);
            resp.Provider = provider;

            // Go: common.Sign(obj, parentPath, encrypt) - sign on parent path for non-dir objects
            if (!obj.IsDir() && (encrypt || signAll))
                resp.Sign = sign.Sign(string.Join("/", parentPath.TrimEnd('/'), obj.GetName()));

            // Go: thumb, _ := model.GetThumb(obj)
            // Go: mountDetails, _ := model.GetStorageDetails(obj) - per-object mount details
            resp.MountDetails = (obj as ObjItem)?.MountDetails;

            if (!obj.IsDir())
            {
                // Go: if storage.Config().MustProxy() || storage.GetStorage().WebProxy
                var mustProxy = getDriver != null && getDriver.Config().MustProxy();
                var webProxy = storage?.WebProxy == true;

                if (mustProxy || webProxy)
                {
                    // Go: rawURL = common.GenerateDownProxyURL(storage.GetStorage(), reqPath)
                    var rawURL = FsHelpers.GenerateDownProxyURL(storage, reqPath, sign);
                    if (string.IsNullOrEmpty(rawURL))
                    {
                        // Go: fallback to /p{path}?sign=...
                        var query = "";
                        if (encrypt || signAll)
                            query = "?sign=" + sign.Sign(reqPath);
                        rawURL = $"{ctx.Request.Scheme}://{ctx.Request.Host}/p{FsHelpers.EncodePath(reqPath)}{query}";
                    }
                    resp.RawURL = rawURL;
                }
                else
                {
                    // Go: if url, ok := model.GetUrl(obj); ok { rawURL = url }
                    if (obj is ObjectURL urlObj && !string.IsNullOrEmpty(urlObj.Url))
                    {
                        resp.RawURL = urlObj.Url;
                    }
                    else if (obj is ObjThumbURL thumbUrlObj && !string.IsNullOrEmpty(thumbUrlObj.Url))
                    {
                        resp.RawURL = thumbUrlObj.Url;
                    }
                    else
                    {
                        // Go: link, _, err := fs.Link(ctx, reqPath, model.LinkArgs{IP, Header, Redirect: true})
                        var link = await fs.LinkAsync(reqPath, new LinkArgs
                        {
                            IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                            Header = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                            Redirect = true
                        });
                        if (link != null)
                            resp.RawURL = link.URL;
                    }
                }
            }

            // Go: getReadme(meta, reqPath) - uses MetaCoversPath
            if (meta != null && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.RSub))
                resp.Readme = meta.Readme;
            // Go: getHeader(meta, reqPath) - uses MetaCoversPath
            if (meta != null && FsHelpers.MetaCoversPath(meta.Path, reqPath, meta.HeaderSub))
                resp.Header = meta.Header;

            // Go: related files - filterRelated(sameLevelFiles, obj)
            if (!obj.IsDir())
            {
                // Go: sameLevelFiles, err := fs.List(ctx, parentPath, &fs.ListArgs{})
                List<IObj> siblingObjs;
                try
                {
                    siblingObjs = await fs.ListAsync(parentPath, new ListArgs { ReqPath = parentPath });
                }
                catch
                {
                    siblingObjs = [];
                }

                if (siblingObjs.Count > 0)
                {
                    // Go: nameWithoutExt := strings.TrimSuffix(obj.GetName(), stdpath.Ext(obj.GetName()))
                    var name = obj.GetName();
                    var ext = System.IO.Path.GetExtension(name);
                    var nameWithoutExt = string.IsNullOrEmpty(ext) ? name : name[..^ext.Length];

                    // Go: isEncrypt(parentMeta, parentPath) for Related sign
                    var parentMeta = await metaSvc.GetNearestMetaAsync(parentPath);
                    var relatedEncrypt = (storage?.EnableSign == true) ||
                        (parentMeta != null && !string.IsNullOrEmpty(parentMeta.Password) && FsHelpers.MetaCoversPath(parentMeta.Path, parentPath, parentMeta.PSub));

                    // Go: filterRelated - objects with same name prefix (excluding self)
                    resp.Related = siblingObjs
                        .Where(o => o.GetName() != name && o.GetName().StartsWith(nameWithoutExt, StringComparison.Ordinal))
                        .Select(o => FsHelpers.MapObjWithSign(o, parentPath, relatedEncrypt, signAll, sign, null)).ToList();
                }
            }

            return Results.Json(new Resp<GetResp>(resp));
        }).AllowAnonymous();
    }
}
