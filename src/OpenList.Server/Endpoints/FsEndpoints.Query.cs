using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static partial class FsEndpoints
{
    internal static void MapFsQueryApi(RouteGroupBuilder group)
    {
        // fs/search - Auth(false): allows guest but not disabled guest
        group.MapMethods("search", ["GET", "POST"], async (ISearchService search, IConfiguration config, IUserService users, IMetaService metaSvc, HttpContext ctx, [FromBody] SearchReq req) =>
        {
            var searchIndex = config["SearchIndex"] ?? "none";
            if (searchIndex == "none")
                return Results.Json(new Resp<object?>(null) { Code = 404, Message = "search not available" });

            // Go: Auth(false) - resolve user (allow anonymous/Guest, but not disabled guest)
            var (u, userId, resolveError) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (resolveError != null) return resolveError;

            req.Parent = FsHelpers.JoinPath(u, req.Parent);

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
                // Go: FsHelpers.CanAccess(user, meta, path.Join(node.Parent, node.Name), req.Password)
                var fullPath = r.Parent.TrimEnd('/') + "/" + r.Name;
                if (!FsHelpers.CanAccess(u, rMeta, fullPath, null, ctx))
                    continue;
                filtered.Add(r);
            }

            return Results.Json(new Resp<SearchResp>(new SearchResp { Content = filtered, Total = total }));
        }).AllowAnonymous().WithSummary("搜索文件");

        // fs/dirs - Auth(false): allows guest but not disabled guest
        group.MapMethods("dirs", ["GET", "POST"], async (IFileSystemService fs, IMetaService metaSvc, IUserService users, HttpContext ctx, [FromBody] DirsReq req) =>
        {
            // Go: Auth(false) - resolve user (allow anonymous/Guest, but not disabled guest)
            var (u, userId, resolveError) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (resolveError != null) return resolveError;

            // Go: ForceRoot check - admin only
            var isAdmin = userId.HasValue && u?.Role == UserRole.Admin;
            if (req.ForceRoot && !isAdmin)
                return Results.Json(Resp<object>.Error(403, "Permission denied"));

            var reqPath = req.Path;
            if (!req.ForceRoot)
                reqPath = FsHelpers.JoinPath(u, req.Path);

            // Go: CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<object>.Error(403, "password is incorrect or you have no permission"));

            var objs = await fs.ListAsync(reqPath);
            var dirs = objs.Where(o => o.IsDir()).Select(o => new DirResp { Name = o.GetName(), Modified = o.GetModTime() }).ToList();
            return Results.Json(new Resp<List<DirResp>>(dirs));
        }).AllowAnonymous().WithSummary("获取子目录列表");

        // fs/other - Auth(false): allows guest but not disabled guest
        group.MapMethods("other", ["GET", "POST"], async (IFileSystemService fs, IUserService users, IMetaService metaSvc, HttpContext ctx, [FromBody] OtherReq req) =>
        {
            // Go: Auth(false) - resolve user (allow anonymous/Guest, but not disabled guest)
            var (u, userId, resolveError) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (resolveError != null) return resolveError;

            var reqPath = FsHelpers.JoinPath(u, req.Path);

            // Go: common.CanAccess - checks Hide, CanRead, and password
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanAccess(u, meta, reqPath, req.Password, ctx))
                return Results.Json(Resp<object>.Error(403, "password is incorrect or you have no permission"));

            var obj = await fs.GetAsync(reqPath);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "object not found"));
            var result = await fs.OtherAsync(new OtherArgs { Obj = obj, Method = req.Method, Data = req.Data });
            return Results.Json(new Resp<object>(result));
        }).AllowAnonymous().WithSummary("执行驱动自定义操作");

        group.MapPost("add_offline_download", async (OfflineDownloadService downloadService, IUserService users, IMetaService metaSvc, ClaimsPrincipal user, AddOfflineDownloadReq req, CancellationToken ct) =>
        {
            var userId = FsHelpers.GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(401, "user not found"));

            if (!u.HasPermission(UserPermission.CanAddOfflineDownloadTasks))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: user.FsHelpers.JoinPath(req.Path)
            var reqPath = FsHelpers.JoinPath(u, req.Path);

            // Go: CanWrite check
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanWrite(u, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            try
            {
                var tasks = await downloadService.AddUrlsAsync(req.Urls ?? [], reqPath, req.Tool, req.DeletePolicy, ct);
                return Results.Json(new Resp<object>(new { tasks }));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(Resp<object>.Error(400, ex.Message));
            }
        }).RequireAuthorization().WithSummary("添加离线下载任务");
    }
}