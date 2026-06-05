using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/me").RequireAuthorization();

        group.MapGet("", async (IUserService users, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<UserResp>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<UserResp>.Error(404, "user not found"));
            return Results.Json(new Resp<UserResp>(new UserResp
            {
                Id = u.Id, Username = u.Username, Role = (int)u.Role,
                BasePath = u.BasePath, Permission = u.Permission,
                Disabled = u.Disabled, Otp = !string.IsNullOrEmpty(u.OtpSecret)
            }));
        });

        group.MapPost("update", async (IUserService users, IAuthService auth, ClaimsPrincipal user, UpdateMeReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var u = await users.GetUserByIdAsync(userId.Value);
            if (u == null) return Results.Json(Resp<object>.Error(404, "user not found"));

            if (req.Username != null) u.Username = req.Username;
            if (req.Password != null) u.PwdHash = auth.HashPassword(req.Password);
            if (req.BasePath != null) u.BasePath = req.BasePath;

            await users.UpdateUserAsync(u);
            return Results.Json(new Resp<object>(null));
        });

        group.MapGet("sshkey/list", async (IUserService users, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<List<SshKey>>.Error(401, "unauthorized"));
            return Results.Json(new Resp<List<SshKey>>(await users.ListSshKeysAsync(userId.Value)));
        });

        group.MapPost("sshkey/add", async (IUserService users, ClaimsPrincipal user, AddSshKeyReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            await users.AddSshKeyAsync(userId.Value, req.Name, req.Key);
            return Results.Json(new Resp<object>(null));
        });

        group.MapPost("sshkey/delete", async (IUserService users, ClaimsPrincipal user, DeleteSshKeyReq req) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            await users.DeleteSshKeyAsync(userId.Value, req.Name);
            return Results.Json(new Resp<object>(null));
        });

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }
}
