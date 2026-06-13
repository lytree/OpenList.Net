using System.IO.Compression;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Shared helper methods for FS endpoints.
/// </summary>
internal static class FsHelpers
{
    internal static Dictionary<string, string>? ParseHashInfo(string? hashStr)
    {
        if (string.IsNullOrEmpty(hashStr) || hashStr == "{}") return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(hashStr); }
        catch { return null; }
    }

    internal static ObjResp MapObj(IObj obj) => new()
    {
        Name = obj.GetName(),
        Size = obj.GetSize(),
        IsDir = obj.IsDir(),
        Modified = obj.GetModTime(),
        Created = obj.GetCreateTime(),
        HashInfoStr = obj.GetHash(),
        HashInfo = ParseHashInfo(obj.GetHash()),
        Type = ObjTypeHelper.GetObjType(obj.GetName(), obj.IsDir()),
        Thumb = ObjTypeHelper.GetThumb(obj),
    };

    internal static ObjResp MapObjWithSign(IObj obj, string parent, bool encrypt, bool signAll, ISignService signSvc, StorageDetails? mountDetails) => new()
    {
        Name = obj.GetName(),
        Size = obj.GetSize(),
        IsDir = obj.IsDir(),
        Modified = obj.GetModTime(),
        Created = obj.GetCreateTime(),
        HashInfoStr = obj.GetHash(),
        HashInfo = ParseHashInfo(obj.GetHash()),
        Type = ObjTypeHelper.GetObjType(obj.GetName(), obj.IsDir()),
        Thumb = ObjTypeHelper.GetThumb(obj),
        // Go: Sign = obj.IsDir() ? "" : (!encrypt && !signAll) ? "" : sign(path)
        Sign = obj.IsDir() ? null : (encrypt || signAll ? signSvc.Sign(string.Join("/", parent.TrimEnd('/'), obj.GetName())) : null),
        MountDetails = obj.IsDir() ? mountDetails : null,
    };

    internal static GetResp MapGetResp(IObj obj) => new()
    {
        Name = obj.GetName(),
        Size = obj.GetSize(),
        IsDir = obj.IsDir(),
        Modified = obj.GetModTime(),
        Created = obj.GetCreateTime(),
        HashInfoStr = obj.GetHash(),
        HashInfo = ParseHashInfo(obj.GetHash()),
        Type = ObjTypeHelper.GetObjType(obj.GetName(), obj.IsDir()),
        Thumb = ObjTypeHelper.GetThumb(obj),
    };

    // Go: EncodePath(path, true) = PathEscape each segment, then join with /
    internal static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    internal static string? GenerateDownProxyURL(Storage? storage, string reqPath, ISignService sign)
    {
        if (storage == null || string.IsNullOrEmpty(storage.DownProxyURL))
            return null;
        var baseURL = storage.DownProxyURL.Split('\n')[0];
        var query = "";
        if (!storage.DisableProxySign)
            query = "?sign=" + sign.Sign(reqPath);
        // Go: EncodePath(reqPath, true) = PathEscape each segment, then join with /
        var encodedPath = EncodePath(reqPath);
        return $"{baseURL}{encodedPath}{query}";
    }

    internal static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }

    /// <summary>
    /// Go: Auth(true) equivalent - resolves user from JWT if present, otherwise returns Guest user.
    /// allowDisabledGuest: when false, disabled guest users get 401 (Go: Auth(false)).
    /// </summary>
    internal static async Task<(User? User, uint? UserId, IResult? Error)> ResolveUserAsync(
        HttpContext ctx, IUserService users, bool allowDisabledGuest = true)
    {
        // Try to get user from authenticated claims
        var userId = GetUserId(ctx.User);
        if (userId.HasValue)
        {
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return (null, null, Results.Json(Resp<object>.Error(401, "user not found")));
            if (u.Disabled)
                return (null, null, Results.Json(Resp<object>.Error(401, "Current user is disabled, replace please")));
            return (u, userId, null);
        }

        // No token → treat as Guest user (Go: Auth gets guest when token is empty)
        var guest = await users.GetGuestUserAsync();
        if (guest == null)
            return (null, null, Results.Json(Resp<object>.Error(500, "guest user not found")));
        if (!allowDisabledGuest && guest.Disabled)
            return (null, null, Results.Json(Resp<object>.Error(401, "Guest user is disabled, login please")));
        return (guest, guest.Id, null);
    }

    /// <summary>
    /// Go: user.JoinPath - prepends user.BasePath if set and not "/"
    /// </summary>
    internal static string JoinPath(User? user, string path)
    {
        if (user != null && !string.IsNullOrEmpty(user.BasePath) && user.BasePath != "/")
            return user.BasePath.TrimEnd('/') + "/" + path.TrimStart('/');
        return path;
    }

    internal static bool UserCanAccessWithoutPassword(HttpContext ctx)
    {
        var user = ctx.Items["User"] as User;
        if (user != null) return user.HasPermission(UserPermission.CanAccessWithoutPassword);
        // Check from claims for authenticated users
        var permStr = ctx.User.FindFirst("Permission")?.Value;
        if (int.TryParse(permStr, out var perm))
            return (perm & (int)UserPermission.CanAccessWithoutPassword) != 0;
        return false;
    }

    internal static bool MetaCoversPath(string metaPath, string targetPath, bool sub)
    {
        if (metaPath == targetPath) return true;
        if (!sub) return false;
        return targetPath.StartsWith(metaPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool CanRead(User? user, Meta? meta, string path)
    {
        if (user == null) return true;
        if (meta != null && meta.ReadUsers.Count > 0 && !meta.ReadUsers.Contains(user.Id) && MetaCoversPath(meta.Path, path, meta.ReadUsersSub))
            return false;
        return true;
    }

    // Go: common.CanAccess - checks Hide, CanRead, and password
    internal static bool CanAccess(User? user, Meta? meta, string reqPath, string? password, HttpContext ctx)
    {
        // Hide check: if user can't see hides and path matches meta.Hide regex
        if (meta != null && (user == null || !user.HasPermission(UserPermission.CanSeeHides)) && !string.IsNullOrEmpty(meta.Hide) &&
            MetaCoversPath(meta.Path, GetParentPath(reqPath), meta.HSub))
        {
            var fileName = System.IO.Path.GetFileName(reqPath);
            foreach (var hidePattern in meta.Hide.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (Regex.IsMatch(fileName, hidePattern))
                        return false;
                }
                catch { /* invalid regex, skip */ }
            }
        }
        // CanRead check
        if (!CanRead(user, meta, reqPath))
            return false;
        // Password check
        if (user != null && user.HasPermission(UserPermission.CanAccessWithoutPassword))
            return true;
        if (meta == null || string.IsNullOrEmpty(meta.Password))
            return true;
        if (!MetaCoversPath(meta.Path, reqPath, meta.PSub))
            return true;
        return meta.Password == password;
    }

    internal static string GetParentPath(string path)
    {
        var idx = path.TrimEnd('/').LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    // Go: utils.IsSystemFile - checks for common system files
    internal static readonly HashSet<string> SystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "desktop.ini", "Thumbs.db", "@eaDir"
    };
    internal static bool IsSystemFile(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        if (SystemFiles.Contains(filename)) return true;
        if (filename.StartsWith("._")) return true;
        return false;
    }

    internal static bool CanWrite(User? user, Meta? meta, string path)
    {
        if (user == null) return true;
        if (meta != null && meta.WriteUsers.Count > 0 && !meta.WriteUsers.Contains(user.Id) && MetaCoversPath(meta.Path, path, meta.WriteUsersSub))
            return false;
        return true;
    }

    internal static (string sid, string path) SplitSharingPath(string sharingPath)
    {
        var trimmed = sharingPath.TrimStart('/');
        var slashIdx = trimmed.IndexOf('/');
        return slashIdx < 0
            ? (trimmed, "")
            : (trimmed[..slashIdx], trimmed[(slashIdx + 1)..]);
    }

    internal static async Task<List<ArchiveEntryInfo>?> ReadArchiveEntriesAsync(IFileSystemService fs, string path, string? password)
    {
        var link = await fs.LinkAsync(path, new LinkArgs());
        if (link == null) return null;

        Stream? archiveStream = null;
        string? tempFile = null;

        try
        {
            // Try to open as local file first
            if (!string.IsNullOrEmpty(link.URL) && File.Exists(link.URL))
            {
                archiveStream = new FileStream(link.URL, FileMode.Open, FileAccess.Read);
            }
            else if (!string.IsNullOrEmpty(link.URL) && link.URL.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Download to temp file
                using var http = new HttpClient();
                var response = await http.GetAsync(link.URL, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                tempFile = Path.Combine(Path.GetTempPath(), $"openlist-archive-{Guid.NewGuid():N}");
                using (var fs2 = File.Create(tempFile))
                    await response.Content.CopyToAsync(fs2);
                archiveStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read);
            }
            else
            {
                return null;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".zip" => ReadZipEntries(archiveStream),
                _ => null // Unsupported archive format
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            archiveStream?.Dispose();
            if (tempFile != null && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    internal static List<ArchiveEntryInfo> ReadZipEntries(Stream stream)
    {
        var entries = new List<ArchiveEntryInfo>();
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            entries.Add(new ArchiveEntryInfo
            {
                Name = entry.FullName,
                Size = entry.Length,
                IsDir = entry.FullName.EndsWith('/'),
                IsEncrypted = entry.CompressedLength == 0 && entry.Length > 0 // heuristic for encrypted entries
            });
        }
        return entries;
    }
}

public class ArchiveEntryInfo
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public bool IsEncrypted { get; set; }
}