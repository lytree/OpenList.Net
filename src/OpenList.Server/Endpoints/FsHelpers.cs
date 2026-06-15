using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Mapster;
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
internal static partial class FsHelpers
{
    internal static Dictionary<string, string>? ParseHashInfo(string? hashStr)
    {
        if (string.IsNullOrEmpty(hashStr) || hashStr == "{}") return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(hashStr); }
        catch { return null; }
    }

    internal static ObjResp MapObj(IObj obj) => obj.Adapt<ObjResp>();

    internal static ObjResp MapObjWithSign(IObj obj, string parent, bool encrypt, bool signAll, ISignService signSvc, StorageDetails? mountDetails)
    {
        var resp = obj.Adapt<ObjResp>();
        // Go: Sign = obj.IsDir() ? "" : (!encrypt && !signAll) ? "" : sign(path)
        resp.Sign = obj.IsDir() ? null : (encrypt || signAll ? signSvc.Sign(string.Join("/", parent.TrimEnd('/'), obj.GetName())) : null);
        // Go: per-object mount details (from ObjStorageDetails wrapper), fallback to parent-level
        resp.MountDetails = obj.IsDir() ? ((obj as ObjItem)?.MountDetails ?? mountDetails) : null;
        return resp;
    }

    internal static GetResp MapGetResp(IObj obj) => obj.Adapt<GetResp>();

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

    /// <summary>
    /// Go: stdpath.Join - joins path elements and normalizes (removes .., ., double slashes).
    /// This is critical for security: prevents path traversal via names like "../secret".
    /// </summary>
    internal static string PathJoin(string basePath, string name)
    {
        // Go equivalent: stdpath.Join(basePath, name)
        var combined = basePath.TrimEnd('/') + "/" + name.TrimStart('/');
        // Use Path.GetFullPath for normalization, then convert back to forward slashes
        try
        {
            // Normalize by splitting and resolving . and ..
            var parts = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var resolved = new List<string>();
            foreach (var part in parts)
            {
                if (part == ".") continue;
                if (part == ".." && resolved.Count > 0) { resolved.RemoveAt(resolved.Count - 1); continue; }
                if (part == "..") continue; // skip .. at root
                resolved.Add(part);
            }
            return "/" + string.Join("/", resolved);
        }
        catch
        {
            return combined;
        }
    }

    /// <summary>
    /// Go: stdpath.Base - returns the last element of the path.
    /// </summary>
    internal static string PathBase(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
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

    /// <summary>
    /// Go: whetherHide - checks if hide patterns should be applied for this user/path.
    /// Admin users (CanSeeHides) bypass hide filtering.
    /// </summary>
    internal static bool WhetherHide(User? user, Meta? meta, string path)
    {
        if (user == null || user.HasPermission(UserPermission.CanSeeHides)) return false;
        if (meta == null) return false;
        if (string.IsNullOrEmpty(meta.Hide)) return false;
        if (!MetaCoversPath(meta.Path, path, meta.HSub)) return false;
        return true;
    }

    /// <summary>
    /// Go: filterReadableObjs - filters objects based on hide patterns and per-object read permissions.
    /// For directories, fetches the nearest meta for each dir path; for files, uses the parent meta.
    /// </summary>
    internal static async Task<List<IObj>> FilterReadableObjsAsync(
        List<IObj> objs, User? user, string reqPath, Meta? parentMeta,
        bool applyHide, IMetaService metaSvc)
    {
        // Compile hide patterns if applicable
        List<Regex>? hidePatterns = null;
        if (applyHide && parentMeta != null && !string.IsNullOrEmpty(parentMeta.Hide))
        {
            hidePatterns = [];
            foreach (var pattern in parentMeta.Hide.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try { hidePatterns.Add(new Regex(pattern)); }
                catch { /* invalid regex, skip */ }
            }
        }

        var result = new List<IObj>(objs.Count);
        foreach (var obj in objs)
        {
            var objName = obj.GetName();

            // Hide check: match filename against hide patterns
            if (hidePatterns != null)
            {
                var hidden = false;
                foreach (var regex in hidePatterns)
                {
                    if (regex.IsMatch(objName)) { hidden = true; break; }
                }
                if (hidden) continue;
            }

            // Per-object read check (Go: filterReadableObjs)
            var objPath = reqPath.TrimEnd('/') + "/" + objName;
            Meta? objMeta;
            if (obj.IsDir())
            {
                // For directories, get nearest meta for the directory path itself
                objMeta = await metaSvc.GetNearestMetaAsync(objPath);
            }
            else
            {
                objMeta = parentMeta;
            }

            if (!CanRead(user, objMeta, objPath)) continue;

            result.Add(obj);
        }
        return result;
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

    // Go: common.CanWriteContentBypassUserPerms(meta, path)
    internal static bool CanWriteContentBypassUserPerms(Meta? meta, string path)
    {
        if (meta == null || !meta.Write) return false;
        return MetaCoversPath(meta.Path, path, meta.WSub);
    }

    internal static (string sid, string path) SplitSharingPath(string sharingPath)
    {
        var trimmed = sharingPath.TrimStart('/');
        var slashIdx = trimmed.IndexOf('/');
        return slashIdx < 0
            ? (trimmed, "")
            : (trimmed[..slashIdx], trimmed[(slashIdx + 1)..]);
    }

    /// <summary>
    /// Unified MIME type lookup used by all endpoint files.
    /// </summary>
    internal static string GetContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4", ".mkv" => "video/x-matroska", ".avi" => "video/x-msvideo",
            ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip", ".tar" => "application/x-tar",
            ".gz" or ".tgz" => "application/gzip", ".rar" => "application/vnd.rar",
            ".7z" => "application/x-7z-compressed",
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png",
            ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml",
            ".txt" => "text/plain", ".html" => "text/html", ".css" => "text/css",
            ".json" => "application/json", ".xml" => "application/xml",
            ".js" => "application/javascript", ".wasm" => "application/wasm",
            _ => "application/octet-stream"
        };
    }
}

