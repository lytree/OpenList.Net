using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class ShareEndpoints
{
    public static IEndpointRouteBuilder MapShareApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/share").RequireAuthorization();

        group.MapMethods("list", ["GET", "POST"], async (ISharingService sharings, IUserService users, ClaimsPrincipal user, int page = 1, int perPage = 100) =>
        {
            var userId = GetUserId(user);
            // Go: AuthNotGuest - guest users cannot access share endpoints
            var u = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            if (u != null && u.IsGuest) return Results.Json(Resp<object>.Error(403, "You are a guest"));
            var isAdmin = user.IsInRole("2");
            // Admin sees all, regular user sees own
            var (sharingList, total) = await sharings.ListSharingsAsync(isAdmin ? 0 : userId ?? 0, page, perPage);
            // Fill CreatorName and CreatorRole (Go: SharingResp)
            foreach (var s in sharingList)
            {
                if (s.Creator != null)
                {
                    s.CreatorName = s.Creator.Username;
                    s.CreatorRole = (int)s.Creator.Role;
                }
            }
            return Results.Json(new Resp<PagedListResp<Sharing>>(new PagedListResp<Sharing> { Content = sharingList, Total = total }));
        });

        // Go: requires auth, only admin or creator can access; returns full SharingResp
        group.MapGet("get", async (ISharingService sharings, ClaimsPrincipal user, string id) =>
        {
            var sharing = await sharings.GetSharingAsync(id);
            if (sharing == null) return Results.Json(Resp<Sharing>.Error(404, "sharing not found"));
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            if (!isAdmin && sharing.CreatorId != userId)
                return Results.Json(Resp<Sharing>.Error(404, "sharing not found"));
            // Fill CreatorName and CreatorRole (Go: SharingResp embeds Sharing + CreatorName + CreatorRole)
            if (sharing.Creator != null)
            {
                sharing.CreatorName = sharing.Creator.Username;
                sharing.CreatorRole = (int)sharing.Creator.Role;
            }
            return Results.Json(new Resp<Sharing>(sharing));
        });

        group.MapPost("create", async (ISharingService sharings, IUserService users, ClaimsPrincipal user, CreateShareReq req) =>
        {
            // Go: AuthNotGuest
            var createUserId = GetUserId(user);
            var createU = createUserId.HasValue ? await users.GetUserByIdAsync(createUserId.Value) : null;
            if (createU != null && createU.IsGuest) return Results.Json(Resp<object>.Error(403, "You are a guest"));

            // Go: must add at least 1 object
            if (req.Files == null || req.Files.Count == 0 || (req.Files.Count == 1 && string.IsNullOrEmpty(req.Files[0])))
                return Results.Json(Resp<Sharing>.Error(400, "must add at least 1 object"));

            // Go: validateSharingID - max 64 chars, only letters, numbers, underscores, hyphens, and CJK
            if (!string.IsNullOrEmpty(req.ID) && !ValidateSharingID(req.ID, out var idErr))
                return Results.Json(Resp<Sharing>.Error(400, idErr));

            var reqUser = user;
            var isAdmin = reqUser.IsInRole("2");
            uint creatorId;
            User? creatorUser = null;
            if (isAdmin && !string.IsNullOrEmpty(req.CreatorName))
            {
                var creator = await users.GetUserByNameAsync(req.CreatorName);
                if (creator == null) return Results.Json(Resp<Sharing>.Error(400, "no such user"));
                creatorId = creator.Id;
                creatorUser = creator;
            }
            else
            {
                creatorId = GetUserId(reqUser) ?? 0;
                creatorUser = await users.GetUserByIdAsync(creatorId);
                // Go: non-admin needs CanShare permission, CanCustomizeShareID for custom ID
                if (creatorUser != null)
                {
                    if (!creatorUser.HasPermission(UserPermission.CanShare))
                        return Results.Json(Resp<Sharing>.Error(403, "permission denied"));
                    if (!string.IsNullOrEmpty(req.ID) && !creatorUser.HasPermission(UserPermission.CanCustomizeShareID))
                        return Results.Json(Resp<Sharing>.Error(403, "permission denied"));
                }
            }

            // Go: non-admin files must be under BasePath
            if (creatorUser != null && !isAdmin)
            {
                for (var i = 0; i < req.Files.Count; i++)
                {
                    var f = req.Files[i].Trim('/');
                    req.Files[i] = "/" + f;
                    if (!string.IsNullOrEmpty(creatorUser.BasePath) && creatorUser.BasePath != "/" &&
                        !req.Files[i].StartsWith(creatorUser.BasePath, StringComparison.OrdinalIgnoreCase))
                        return Results.Json(Resp<Sharing>.Error(403, $"permission denied to share path [{req.Files[i]}]"));
                }
            }

            var sharing = new Sharing
            {
                Id = req.ID ?? "",
                FilesRaw = System.Text.Json.JsonSerializer.Serialize(req.Files),
                Pwd = req.Pwd, Expires = req.Expires,
                MaxAccessed = req.MaxAccessed, Disabled = req.Disabled,
                Remark = req.Remark, Readme = req.Readme,
                Header = req.Header, CreatorId = creatorId, OrderBy = req.OrderBy,
                OrderDirection = req.OrderDirection, ExtractFolder = req.ExtractFolder
            };
            var created = await sharings.CreateSharingAsync(sharing);
            // Go: SharingResp includes CreatorName and CreatorRole
            if (creatorUser != null)
            {
                created.CreatorName = creatorUser.Username;
                created.CreatorRole = (int)creatorUser.Role;
            }
            return Results.Json(new Resp<Sharing>(created));
        });

        group.MapPost("update", async (ISharingService sharings, IUserService users, ClaimsPrincipal user, UpdateShareReq req) =>
        {
            // Go: must add at least 1 object
            if (req.Files == null || req.Files.Count == 0 || (req.Files.Count == 1 && string.IsNullOrEmpty(req.Files[0])))
                return Results.Json(Resp<object>.Error(400, "must add at least 1 object"));

            var reqUser = user;
            var isAdmin = reqUser.IsInRole("2");
            var userId = GetUserId(reqUser);

            // Go: check CanShare for non-admin
            var reqUserModel = userId.HasValue ? await users.GetUserByIdAsync(userId.Value) : null;
            if (!isAdmin && reqUserModel != null && !reqUserModel.HasPermission(UserPermission.CanShare))
                return Results.Json(Resp<object>.Error(403, "permission denied"));

            // Go: only admin or creator can update
            var existing = await sharings.GetSharingAsync(req.Id);
            if (existing == null) return Results.Json(Resp<object>.Error(404, "sharing not found"));
            if (!isAdmin && existing.CreatorId != userId)
                return Results.Json(Resp<object>.Error(404, "sharing not found"));

            // Go: admin can specify CreatorName; without it, keep original creator
            User? creatorUser = reqUserModel;
            if (isAdmin && !string.IsNullOrEmpty(req.CreatorName))
            {
                var creator = await users.GetUserByNameAsync(req.CreatorName);
                if (creator == null) return Results.Json(Resp<object>.Error(400, "no such user"));
                creatorUser = creator;
            }
            else if (isAdmin && string.IsNullOrEmpty(req.CreatorName))
            {
                creatorUser = existing.Creator;
            }

            // Go: non-admin files must be under BasePath
            if (reqUserModel != null && !isAdmin)
            {
                for (var i = 0; i < req.Files.Count; i++)
                {
                    var f = req.Files[i].Trim('/');
                    req.Files[i] = "/" + f;
                    if (!string.IsNullOrEmpty(reqUserModel.BasePath) && reqUserModel.BasePath != "/" &&
                        !req.Files[i].StartsWith(reqUserModel.BasePath, StringComparison.OrdinalIgnoreCase))
                        return Results.Json(Resp<object>.Error(403, $"permission denied to share path [{req.Files[i]}]"));
                }
            }

            // Go: NewID change requires CanCustomizeShareID
            if (!string.IsNullOrEmpty(req.NewID) && req.NewID != req.Id)
            {
                if (reqUserModel == null || (!isAdmin && !reqUserModel.HasPermission(UserPermission.CanCustomizeShareID)))
                    return Results.Json(Resp<object>.Error(403, "permission denied"));
                // Go: validateSharingID for NewID
                if (!ValidateSharingID(req.NewID, out var newIdErr))
                    return Results.Json(Resp<object>.Error(400, newIdErr));
                // Update ID first
                await sharings.UpdateSharingIdAsync(req.Id, req.NewID);
                req.Id = req.NewID;
            }

            // Update fields
            existing.FilesRaw = System.Text.Json.JsonSerializer.Serialize(req.Files);
            existing.Expires = req.Expires;
            existing.Pwd = req.Pwd;
            existing.Accessed = req.Accessed;
            existing.MaxAccessed = req.MaxAccessed;
            existing.Disabled = req.Disabled;
            existing.OrderBy = req.OrderBy;
            existing.OrderDirection = req.OrderDirection;
            existing.ExtractFolder = req.ExtractFolder;
            existing.Header = req.Header;
            existing.Readme = req.Readme;
            existing.Remark = req.Remark;
            // Go: update Creator if admin specified CreatorName
            if (creatorUser != null) existing.CreatorId = creatorUser.Id;

            await sharings.UpdateSharingAsync(existing);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("delete", async (ISharingService sharings, ClaimsPrincipal user, string id) =>
        {
            // Go: only admin or creator can delete
            var existing = await sharings.GetSharingAsync(id);
            if (existing == null) return Results.Json(Resp<object>.Error(404, "sharing not found"));
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            if (!isAdmin && existing.CreatorId != userId)
                return Results.Json(Resp<object>.Error(404, "sharing not found"));
            await sharings.DeleteSharingAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("enable", async (ISharingService sharings, ClaimsPrincipal user, string id) =>
        {
            // Go: only admin or creator can enable
            var existing = await sharings.GetSharingAsync(id);
            if (existing == null) return Results.Json(Resp<object>.Error(404, "sharing not found"));
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            if (!isAdmin && existing.CreatorId != userId)
                return Results.Json(Resp<object>.Error(404, "sharing not found"));
            await sharings.EnableSharingAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("disable", async (ISharingService sharings, ClaimsPrincipal user, string id) =>
        {
            // Go: only admin or creator can disable
            var existing = await sharings.GetSharingAsync(id);
            if (existing == null) return Results.Json(Resp<object>.Error(404, "sharing not found"));
            var userId = GetUserId(user);
            var isAdmin = user.IsInRole("2");
            if (!isAdmin && existing.CreatorId != userId)
                return Results.Json(Resp<object>.Error(404, "sharing not found"));
            await sharings.DisableSharingAsync(id);
            return Results.Json(new Resp<object>(null));
        });

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }

    // Go: validateSharingID - max 64 chars, only letters, numbers, underscores, hyphens, and CJK
    private static readonly System.Text.RegularExpressions.Regex ValidSharingIDRegex = new(@"^[\w\p{IsCJKUnifiedIdeographs}\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool ValidateSharingID(string id, out string error)
    {
        error = "";
        if (id.Length > 64) { error = "share id must be at most 64 characters"; return false; }
        if (!ValidSharingIDRegex.IsMatch(id)) { error = "share id can only contain letters, numbers, underscores, hyphens, and CJK characters"; return false; }
        return true;
    }
}
