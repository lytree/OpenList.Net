using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class FsEndpoints
{
    public static IEndpointRouteBuilder MapFsApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/fs");

        group.MapPost("list", async (IFileSystemService fs, ListReq req) =>
        {
            var args = new ListArgs { ReqPath = req.Path, Refresh = req.Refresh, WithStorageDetails = req.WithStorageDetails };
            var objs = await fs.ListAsync(req.Path, args);
            return Results.Json(new Resp<ListResp>(new ListResp { Content = objs.Select(MapObj).ToList() }));
        });

        group.MapPost("get", async (IFileSystemService fs, ISignService sign, HttpContext ctx, GetReq req) =>
        {
            var obj = await fs.GetAsync(req.Path);
            if (obj == null) return Results.Json(Resp<GetResp>.Error(404, "object not found"));
            var resp = new GetResp { Obj = MapObj(obj) };
            if (!obj.IsDir())
            {
                var link = await fs.LinkAsync(req.Path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
                if (link != null)
                {
                    resp.RawURL = link.URL;
                    resp.Header = link.Header;
                }
            }
            return Results.Json(new Resp<GetResp>(resp));
        });

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

        group.MapPost("put", (PutReq req) =>
        {
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("link", async (IFileSystemService fs, HttpContext ctx, LinkReq req) =>
        {
            var link = await fs.LinkAsync(req.Path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
            if (link == null) return Results.Json(Resp<LinkResp>.Error(500, "failed to get link"));
            return Results.Json(new Resp<LinkResp>(new LinkResp { URL = link.URL, Header = link.Header }));
        }).RequireAuthorization();

        group.MapPost("search", async (ISearchService search, SearchReq req) =>
        {
            var results = await search.SearchAsync(req.Parent, req.Keywords, req.Page, req.PerPage);
            return Results.Json(new Resp<SearchResp>(new SearchResp { Content = results }));
        });

        group.MapPost("dirs", async (IFileSystemService fs, DirsReq req) =>
        {
            var objs = await fs.ListAsync(req.Path);
            var dirs = objs.Where(o => o.IsDir()).Select(MapObj).ToList();
            return Results.Json(new Resp<DirsResp>(new DirsResp { Content = dirs }));
        });

        group.MapPost("other", async (IFileSystemService fs, OtherReq req) =>
        {
            var obj = await fs.GetAsync(req.Path);
            if (obj == null) return Results.Json(Resp<object>.Error(404, "object not found"));
            var result = await fs.OtherAsync(new OtherArgs { Obj = obj, Method = req.Method, Data = req.Data });
            return Results.Json(new Resp<object>(result));
        });

        group.MapPost("add_offline_download", async (ITaskService taskService, AddOfflineDownloadReq req) =>
        {
            var task = await taskService.CreateTaskAsync("offline_download", $"Download {string.Join(", ", req.Urls ?? [])}");
            return Results.Json(new Resp<object>(new { task_id = task.Id }));
        }).RequireAuthorization();

        group.MapPost("archive/decompress", async (ITaskService taskService, ArchiveDecompressReq req) =>
        {
            var task = await taskService.CreateTaskAsync("decompress", $"Decompress {req.SrcDir}/{req.Name}");
            return Results.Json(new Resp<object>(new { task_id = task.Id }));
        }).RequireAuthorization();

        group.MapPost("torrent/parse", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/upload_parse", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/rapid_upload", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();
        group.MapPost("torrent/generate", () => Results.Json(Resp<object>.Error(501, "not implemented"))).RequireAuthorization();

        group.MapPost("get_direct_upload_info", (DirectUploadReq req) =>
        {
            return Results.Json(new Resp<DirectUploadInfo>(new DirectUploadInfo
            {
                URL = "",
                Header = new Dictionary<string, string>()
            }));
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
        Hash = obj.GetHash(),
        ID = obj.GetID(),
        Path = obj.GetPath()
    };
}
