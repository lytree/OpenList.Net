using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace OpenList.Server.Endpoints;

public static partial class FsEndpoints
{
    public static IEndpointRouteBuilder MapFsApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/fs");

        MapFsListApi(group);
        MapFsWriteApi(group);
        MapFsUploadApi(group);
        MapFsArchiveApi(group);
        MapFsQueryApi(group);

        return endpoints;
    }
}
