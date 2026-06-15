using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace OpenList.Server.Endpoints;

// FsEndpoints is split across multiple partial class files:
// - FsEndpoints.List.cs     (list, get)
// - FsEndpoints.Write.cs    (mkdir, rename, batch_rename, regex_rename, move, recursive_move, copy, remove, remove_empty_directory)
// - FsEndpoints.Upload.cs   (put, form, link, get_direct_upload_info)
// - FsEndpoints.Archive.cs  (archive/meta, archive/list, archive/decompress)
// - FsEndpoints.Query.cs    (search, dirs, other, add_offline_download)
// - FsEndpoints.Torrent.cs  (torrent/parse, torrent/upload_parse, torrent/rapid_upload, torrent/generate)

public static partial class FsEndpoints
{
    /// <summary>
    /// Registers all /api/fs endpoints.
    /// Go: fsAndShare(api.Group("/fs", Auth(true))) + _fs(auth.Group("/fs"))
    /// - fsAndShare: list, get, archive/meta, archive/list (allows guest)
    /// - _fs: all other fs operations (requires auth, not guest)
    /// </summary>
    public static IEndpointRouteBuilder MapFsApi(this IEndpointRouteBuilder endpoints)
    {
        var fs = endpoints.MapGroup("/api/fs");

        // Go: fsAndShare - Auth(true) allows guest access
        // Individual endpoints use AllowAnonymous() + ResolveUserAsync internally
        MapFsListApi(fs);

        // Go: fsAndShare - archive sub-group (Auth(true))
        MapFsArchiveApi(fs);

        // Go: _fs - Auth(false) requires authentication
        // Individual endpoints use RequireAuthorization()
        MapFsWriteApi(fs);
        MapFsUploadApi(fs);
        MapFsQueryApi(fs);
        MapFsTorrentApi(fs);

        return endpoints;
    }
}
