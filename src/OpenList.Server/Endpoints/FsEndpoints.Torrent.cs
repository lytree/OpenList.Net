using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static partial class FsEndpoints
{
    // Max Base64-encoded torrent size (~10MB decoded)
    private const int MaxTorrentBase64Len = 14 * 1024 * 1024;
    // Max file size for synchronous torrent generation (1GB)
    private const long MaxTorrentGenFileSize = 1L * 1024 * 1024 * 1024;

    internal static void MapFsTorrentApi(RouteGroupBuilder group)
    {
        var torrentGroup = group.MapGroup("torrent");

        // POST torrent/parse - parse torrent file from Base64 data (Go: ParseTorrent)
        torrentGroup.MapPost("parse", (TorrentService torrentSvc, [FromBody] TorrentParseReq req) =>
        {
            if (string.IsNullOrEmpty(req.TorrentData))
                return Results.Json(Resp<object>.Error(400, "torrent_data is required"));

            if (req.TorrentData.Length > MaxTorrentBase64Len)
                return Results.Json(Resp<object>.Error(400, "torrent data too large (max 10MB)"));

            byte[] torrentData;
            try { torrentData = Convert.FromBase64String(req.TorrentData); }
            catch (FormatException) { return Results.Json(Resp<object>.Error(400, "invalid Base64 encoding")); }

            TorrentInfo t;
            try { t = torrentSvc.Decode(torrentData); }
            catch (Exception ex) { return Results.Json(Resp<object>.Error(400, $"failed to parse torrent: {ex.Message}")); }

            if (t.PieceCount > 0 && (t.PieceCount * 20) % 20 != 0)
                return Results.Json(Resp<object>.Error(400, "invalid torrent pieces data"));

            var resp = new TorrentParseResp
            {
                Name = t.Name,
                TotalSize = t.TotalSize,
                PieceLength = t.PieceLength,
                PieceCount = t.PieceCount,
                InfoHash = t.InfoHash,
                HasCAS = t.HasCAS,
                Files = t.Files.Select(f => new TorrentFileInfo { Path = f.Path, Size = f.Size }).ToList()
            };
            if (t.CAS != null)
                resp.CAS = new CASInfoResp { FileMD5 = t.CAS.FileMD5, SliceMD5 = t.CAS.SliceMD5, SliceSize = t.CAS.SliceSize, Cloud = t.CAS.Cloud };

            return Results.Json(new Resp<TorrentParseResp>(resp));
        }).RequireAuthorization().WithSummary("解析 Torrent 文件 (Base64)");

        // POST torrent/upload_parse - upload and parse torrent file (Go: UploadTorrentAndParse)
        torrentGroup.MapPost("upload_parse", async (TorrentService torrentSvc, HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.Json(Resp<object>.Error(400, "expected multipart form data"));

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("torrent");
            if (file == null)
                return Results.Json(Resp<object>.Error(400, "torrent file not found"));

            if (file.Length > 10 * 1024 * 1024)
                return Results.Json(Resp<object>.Error(400, "torrent file too large (max 10MB)"));

            byte[] torrentData;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                torrentData = ms.ToArray();
            }

            TorrentInfo t;
            try { t = torrentSvc.Decode(torrentData); }
            catch (Exception ex) { return Results.Json(Resp<object>.Error(400, $"failed to parse torrent: {ex.Message}")); }

            if (t.PieceCount > 0 && (t.PieceCount * 20) % 20 != 0)
                return Results.Json(Resp<object>.Error(400, "invalid torrent pieces data"));

            var info = new TorrentParseResp
            {
                Name = t.Name,
                TotalSize = t.TotalSize,
                PieceLength = t.PieceLength,
                PieceCount = t.PieceCount,
                InfoHash = t.InfoHash,
                HasCAS = t.HasCAS,
                Files = t.Files.Select(f => new TorrentFileInfo { Path = f.Path, Size = f.Size }).ToList()
            };
            if (t.CAS != null)
                info.CAS = new CASInfoResp { FileMD5 = t.CAS.FileMD5, SliceMD5 = t.CAS.SliceMD5, SliceSize = t.CAS.SliceSize, Cloud = t.CAS.Cloud };

            return Results.Json(new Resp<object>(new
            {
                info,
                torrent_data = Convert.ToBase64String(torrentData)
            }));
        }).RequireAuthorization().WithSummary("上传并解析 Torrent 文件");

        // POST torrent/rapid_upload - CAS rapid upload from torrent (Go: TorrentRapidUpload, 189PC only)
        torrentGroup.MapPost("rapid_upload", async (
            IFileSystemService fs, IUserService users, IMetaService metaSvc,
            HttpContext ctx, [FromBody] TorrentRapidUploadReq req) =>
        {
            var (user, _, error) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (error != null) return error;

            if (string.IsNullOrEmpty(req.TorrentData))
                return Results.Json(Resp<object>.Error(400, "torrent_data is required"));
            if (string.IsNullOrEmpty(req.Path))
                return Results.Json(Resp<object>.Error(400, "path is required"));

            var reqPath = FsHelpers.JoinPath(user, req.Path);

            // Go: write permission check
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanWrite(user, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // CAS rapid upload requires a specific cloud driver (189PC) — not available without cloud drivers
            return Results.Json(Resp<object>.Error(400, "rapid upload requires a cloud storage driver that supports CAS (e.g. 189PC), no compatible driver is currently available"));
        }).RequireAuthorization().WithSummary("Torrent 快速上传 (CAS)");

        // POST torrent/generate - generate torrent for a file path (Go: GenerateTorrentForPath)
        torrentGroup.MapPost("generate", async (
            TorrentService torrentSvc, IFileSystemService fs, IUserService users, IMetaService metaSvc,
            HttpContext ctx, [FromBody] TorrentGenerateReq req) =>
        {
            var (user, _, error) = await FsHelpers.ResolveUserAsync(ctx, users, allowDisabledGuest: false);
            if (error != null) return error;

            if (string.IsNullOrEmpty(req.Path))
                return Results.Json(Resp<object>.Error(400, "path is required"));

            var reqPath = FsHelpers.JoinPath(user, req.Path);

            // Go: read permission check
            var meta = await metaSvc.GetNearestMetaAsync(reqPath);
            if (!FsHelpers.CanRead(user, meta, reqPath))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            var obj = await fs.GetAsync(reqPath);
            if (obj == null)
                return Results.Json(Resp<object>.Error(404, "file not found"));
            if (obj.IsDir())
                return Results.Json(Resp<object>.Error(400, "cannot generate torrent for directory"));

            if (obj.GetSize() > MaxTorrentGenFileSize)
                return Results.Json(Resp<object>.Error(400, "file too large for torrent generation (max 1GB)"));

            // Get file link/stream
            var link = await fs.LinkAsync(reqPath, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
            if (link == null)
                return Results.Json(Resp<object>.Error(500, "failed to get file link"));

            byte[] torrentData;
            try
            {
                // If local file, read directly; otherwise use HTTP stream
                Stream contentStream;
                if (!string.IsNullOrEmpty(link.URL) && !link.URL.StartsWith("http"))
                {
                    if (!File.Exists(link.URL))
                        return Results.Json(Resp<object>.Error(404, "file not accessible"));
                    contentStream = File.OpenRead(link.URL);
                }
                else if (!string.IsNullOrEmpty(link.URL) && link.URL.StartsWith("http"))
                {
                    var httpClient = new HttpClient();
                    foreach (var header in link.Header ?? new())
                        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    contentStream = await httpClient.GetStreamAsync(link.URL, ctx.RequestAborted);
                }
                else
                {
                    return Results.Json(Resp<object>.Error(400, "storage does not support streaming, cannot generate torrent"));
                }

                using (contentStream)
                {
                    torrentData = torrentSvc.Generate(obj.GetName(), obj.GetSize(), contentStream, req.WithCAS);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(Resp<object>.Error(500, $"failed to generate torrent: {ex.Message}"));
            }

            // Decode the generated torrent to get info_hash
            var generated = torrentSvc.Decode(torrentData);

            return Results.Json(new Resp<object>(new
            {
                torrent_data = Convert.ToBase64String(torrentData),
                info_hash = generated.InfoHash,
                file_name = obj.GetName() + ".torrent",
                size = torrentData.Length,
                with_cas = req.WithCAS
            }));
        }).RequireAuthorization().WithSummary("生成 Torrent 文件");
    }
}

// === Torrent DTOs ===

public class TorrentParseReq
{
    [JsonPropertyName("torrent_data")]
    public string TorrentData { get; set; } = "";
}

public class TorrentParseResp
{
    public string Name { get; set; } = "";
    [JsonPropertyName("total_size")]
    public long TotalSize { get; set; }
    [JsonPropertyName("piece_length")]
    public long PieceLength { get; set; }
    [JsonPropertyName("piece_count")]
    public int PieceCount { get; set; }
    [JsonPropertyName("info_hash")]
    public string InfoHash { get; set; } = "";
    public List<TorrentFileInfo> Files { get; set; } = [];
    [JsonPropertyName("has_cas")]
    public bool HasCAS { get; set; }
    public CASInfoResp? CAS { get; set; }
}

public class TorrentFileInfo
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
}

public class CASInfoResp
{
    [JsonPropertyName("file_md5")]
    public string FileMD5 { get; set; } = "";
    [JsonPropertyName("slice_md5")]
    public string SliceMD5 { get; set; } = "";
    [JsonPropertyName("slice_size")]
    public long SliceSize { get; set; }
    public string Cloud { get; set; } = "";
}

public class TorrentRapidUploadReq
{
    [JsonPropertyName("torrent_data")]
    public string TorrentData { get; set; } = "";
    public string Path { get; set; } = "";
}

public class TorrentGenerateReq
{
    public string Path { get; set; } = "";
    [JsonPropertyName("with_cas")]
    public bool WithCAS { get; set; }
}
