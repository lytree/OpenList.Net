using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Auth endpoints: login, 2FA, logout, current user, hash_password (admin).
/// Mirrors OpenList Go <c>server/handles/auth.go</c>.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginReq req, IAuthService auth) =>
        {
            var r = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        g.MapPost("/login/2fa", async (Login2FaReq req, IAuthService auth) =>
        {
            var r = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        g.MapPost("/login/hash", async (LoginReq req, IAuthService auth) =>
        {
            var r = await auth.LoginHashAsync(req.Username, req.Password, req.OtpCode);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        // POST /api/auth/login/ldap — LDAP login. Same body as /login/hash.
        // The frontend's login page toggles this when the user enables LDAP mode.
        // The service currently returns 501 until an LDAP backend is wired up.
        g.MapPost("/login/ldap", async (LoginReq req, IAuthService auth) =>
        {
            var r = await auth.LoginLdapAsync(req.Username, req.Password);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        g.MapPost("/logout", async (HttpContext ctx, IAuthService auth) =>
        {
            var token = ExtractToken(ctx);
            if (token is null) return Results.Ok(Resp.Fail("no token", RespCodes.Unauthorized));
            await auth.LogoutAsync(token);
            return Results.Ok(Resp.Ok());
        }).RequireAuthorization();

        g.MapGet("/me", async (ClaimsPrincipal user, IUserService users) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp<UserPublic>.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await users.GetCurrentAsync(id.Value);
            return Results.Ok(r);
        }).RequireAuthorization();

        g.MapPost("/2fa/generate", async (ClaimsPrincipal user, IAuthService auth) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp<Generate2FaResp>.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await auth.Generate2FaAsync(id.Value);
            if (r.Code != 200) return Results.Ok(r);
            var resp = new Generate2FaResp
            {
                Secret = r.Data!,
                QrUri = $"otpauth://totp/OpenList:{user.Identity?.Name}?secret={r.Data}&issuer=OpenList",
            };
            return Results.Ok(Resp<Generate2FaResp>.Ok(resp));
        }).RequireAuthorization();

        g.MapPost("/2fa/verify", async (ClaimsPrincipal user, Verify2FaReq req, IAuthService auth) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await auth.Verify2FaAsync(id.Value, req.Code);
            return Results.Ok(r);
        }).RequireAuthorization();

        g.MapPost("/hash_password", (HashPasswordReq req) =>
            Results.Ok(Resp<HashPasswordResp>.Ok(new HashPasswordResp { Hash = UserService.HashPassword(req.Password) })))
         .RequireAuthorization("admin");

        // --- SSO login ---
        // Routes mirror OpenList Go (server/router.go): a single /auth/sso entry point
        // that dispatches by the ?method= query param, plus the callback variants.
        // The frontend builds absolute URLs like `<base>/api/auth/sso?method=sso_get_token`.
        //
        // Flow:
        //   1. Browser hits GET /api/auth/sso?method=get_sso_id|sso_get_token
        //   2. Server 302-redirects to the IdP authorize URL with our client_id.
        //   3. IdP redirects back to GET /api/auth/sso_callback?method=...&code=...&state=...
        //   4. Server exchanges the code, fetches the user profile, and either
        //      returns {sso_id} (link-account mode) or {token} (login mode) as HTML
        //      that postMessages to the opener and closes the popup — matching the
        //      frontend's window.opener.postMessage handler in Profile.tsx.
        g.MapGet("/sso", async (string? method, HttpContext ctx, ISsoService sso) =>
        {
            if (string.IsNullOrEmpty(method))
                return Results.Ok(Resp.Fail("no method provided", RespCodes.BadRequest));
            var redirectUri = BuildSsoCallbackUrl(ctx, method);
            var r = await sso.GetAuthorizeUrlAsync(method, redirectUri);
            if (r.Code != RespCodes.Ok) return Results.Ok(r);
            return Results.Redirect(r.Data!);
        }).AllowAnonymous();

        g.MapGet("/sso_callback", async (string? method, string? code, string? state, HttpContext ctx, ISsoService sso) =>
        {
            // Compatibility mode: method is encoded in the URL path instead of a query param.
            var effectiveMethod = method ?? LastUrlSegment(ctx.Request.Path.Value);
            if (string.IsNullOrEmpty(effectiveMethod))
                return Results.Ok(Resp.Fail("no method provided", RespCodes.BadRequest));
            var redirectUri = BuildSsoCallbackUrl(ctx, effectiveMethod);
            var r = await sso.HandleCallbackAsync(effectiveMethod, code ?? "", state, redirectUri);
            if (r.Code != RespCodes.Ok) return Results.Ok(r);
            return Results.Text(RenderSsoCallbackHtml(r.Data!), "text/html; charset=utf-8");
        }).AllowAnonymous();

        // Aliases matching Go's dedicated callback routes (compatibility mode paths).
        g.MapGet("/get_sso_id", (string? code, string? state, HttpContext ctx, ISsoService sso) =>
            HandleCompatCallbackAsync("get_sso_id", code, state, ctx, sso)).AllowAnonymous();
        g.MapGet("/sso_get_token", (string? code, string? state, HttpContext ctx, ISsoService sso) =>
            HandleCompatCallbackAsync("sso_get_token", code, state, ctx, sso)).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> HandleCompatCallbackAsync(string method, string? code, string? state, HttpContext ctx, ISsoService sso)
    {
        // In compatibility mode the IdP redirects to /api/auth/get_sso_id (no ?method=).
        // The redirect_uri passed to the IdP was built from this same path, so we
        // reconstruct it here for the token exchange to validate.
        var redirectUri = $"{GetApiBaseUrl(ctx)}/api/auth/{method}";
        var r = await sso.HandleCallbackAsync(method, code ?? "", state, redirectUri);
        if (r.Code != RespCodes.Ok) return Results.Ok(r);
        return Results.Text(RenderSsoCallbackHtml(r.Data!), "text/html; charset=utf-8");
    }

    private static string BuildSsoCallbackUrl(HttpContext ctx, string method)
    {
        // Non-compatibility mode: always /api/auth/sso_callback?method=...
        return $"{GetApiBaseUrl(ctx)}/api/auth/sso_callback?method={Uri.EscapeDataString(method)}";
    }

    private static string GetApiBaseUrl(HttpContext ctx)
    {
        var req = ctx.Request;
        // Honour X-Forwarded-* headers when behind a reverse proxy.
        var scheme = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
        var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.Value!;
        return $"{scheme}://{host}";
    }

    private static string? LastUrlSegment(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var seg = path.TrimEnd('/').Split('/').LastOrDefault();
        return string.IsNullOrEmpty(seg) ? null : seg;
    }

    /// <summary>
    /// Emits the same HTML the Go server returns: a tiny page that postMessages
    /// either {sso_id} or {token} to the opener and closes itself. The frontend
    /// (Profile.tsx / Login) listens for this via <c>window.addEventListener('message', ...)</c>.
    /// </summary>
    private static string RenderSsoCallbackHtml(SsoCallbackResult r)
    {
        var payload = r.Token is not null
            ? $"{{\"token\":\"{EscapeJs(r.Token)}\"}}"
            : $"{{\"sso_id\":\"{EscapeJs(r.SsoId ?? string.Empty)}\"}}";
        return $@"<!DOCTYPE html><head></head><body><script>
window.opener.postMessage({payload}, ""*"");
window.close();
</script></body>";
    }

    private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("<", "\\u003c");

    internal static string? ExtractToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth)) return null;
        // Frontend sends token WITHOUT "Bearer " prefix — accept both.
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : auth;
    }
}

/// <summary>
/// WebAuthn (passkey) endpoints. Mirrors OpenList Go
/// <c>server/handles/webauthn.go</c> + <c>server/router.go</c> authn group.
/// Frontend callers (login/index.tsx, manage/users/Profile.tsx,
/// manage/users/Webauthnitems.tsx) drive the flow:
///   login:    GET  /authn/webauthn_begin_login?username=
///             POST /authn/webauthn_finish_login?username=  (Session header)
///   register: GET  /authn/webauthn_begin_registration     (auth'd)
///             POST /authn/webauthn_finish_registration     (auth'd, Session header)
///   manage:   GET  /authn/getcredentials                  (auth'd)
///             POST /authn/delete_authn  {id}              (auth'd)
/// The Session header carries a base64(UTF8 JSON) blob produced by
/// <see cref="IWebAuthnService.BeginLoginAsync"/> /
/// <see cref="IWebAuthnService.BeginRegistrationAsync"/>, echoed verbatim by
/// the browser between the begin and finish calls.
/// </summary>
public static class AuthnEndpoints
{
    public static IEndpointRouteBuilder MapAuthnApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/authn").WithTags("Authn");

        // --- Login (public) ---
        // username may be empty for discoverable (conditional mediation) login.
        g.MapGet("/webauthn_begin_login", async (string? username, IWebAuthnService svc) =>
            Results.Ok(await svc.BeginLoginAsync(username)))
         .AllowAnonymous();

        g.MapPost("/webauthn_finish_login", async (HttpContext ctx, IWebAuthnService svc) =>
        {
            var username = ctx.Request.Query["username"].FirstOrDefault();
            var session = ctx.Request.Headers["session"].ToString();
            var body = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            return Results.Ok(await svc.FinishLoginAsync(username, session, body));
        })
         .AllowAnonymous();

        // --- Registration (auth'd) ---
        g.MapGet("/webauthn_begin_registration", async (ClaimsPrincipal user, IWebAuthnService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Unauthorized();
            return Results.Ok(await svc.BeginRegistrationAsync(id.Value));
        })
         .RequireAuthorization();

        g.MapPost("/webauthn_finish_registration", async (HttpContext ctx, ClaimsPrincipal user, IWebAuthnService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Unauthorized();
            var session = ctx.Request.Headers["session"].ToString();
            var body = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            return Results.Ok(await svc.FinishRegistrationAsync(id.Value, session, body));
        })
         .RequireAuthorization();

        // --- Credential management (auth'd) ---
        // Returns [{id, fingerprint}] — matches the frontend WebauthnItem shape
        // (Go returns id as []byte → base64 string; we store CredentialId as
        // base64url already). fingerprint is the authenticator AAGUID.
        g.MapGet("/getcredentials", async (ClaimsPrincipal user, IWebAuthnService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Unauthorized();
            var r = await svc.ListCredentialsAsync(id.Value);
            if (r.Code != RespCodes.Ok) return Results.Ok(r);
            var view = r.Data!.Select(c => new { id = c.CredentialId, fingerprint = c.Aaguid }).ToList();
            return Results.Ok(Resp<object>.Ok(view));
        })
         .RequireAuthorization();

        g.MapPost("/delete_authn", async (DeleteAuthnReq req, ClaimsPrincipal user, IWebAuthnService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Unauthorized();
            return Results.Ok(await svc.DeleteCredentialAsync(id.Value, req.Id));
        })
         .RequireAuthorization();

        return app;
    }
}

/// <summary>Body for <c>POST /api/authn/delete_authn</c>.</summary>
public sealed class DeleteAuthnReq
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>Extension helpers for extracting user identity from <see cref="ClaimsPrincipal"/>.</summary>
public static class AuthClaimsExtensions
{
    public static long? UserId(this ClaimsPrincipal principal)
    {
        var v = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(v, out var id) ? id : null;
    }

    public static UserRole UserRole(this ClaimsPrincipal principal)
    {
        var v = principal.FindFirst(ClaimTypes.Role)?.Value;
        return int.TryParse(v, out var r) ? (UserRole)r : Core.Models.UserRole.Guest;
    }

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.UserRole() == Core.Models.UserRole.Admin;

    /// <summary>Build a minimal <see cref="User"/> from claims for authorization checks in services.</summary>
    public static User ToUser(this ClaimsPrincipal principal)
    {
        var id = principal.UserId() ?? 0;
        return new User
        {
            Id = id,
            Username = principal.Identity?.Name ?? string.Empty,
            Role = principal.UserRole(),
        };
    }
}
