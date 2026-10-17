using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 认证端点：登录、2FA、登出、获取当前用户、密码哈希（admin）。
/// 对应 OpenList Go <c>server/handles/auth.go</c>。
/// 路由位于 <c>/api/auth</c>：/login、/login/2fa、/login/hash、/login/ldap、
/// /logout、/me、/2fa/generate、/2fa/verify、/hash_password、/sso 等。
/// 登录类端点匿名可访问，其余需鉴权（/hash_password 需管理员角色）。
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// 注册 <c>/api/auth</c> 认证路由组。登录/SSO 端点匿名，其余需要 Bearer JWT；
    /// /hash_password 额外要求 admin 角色。
    /// </summary>
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        // POST /api/auth/login — 用户名/密码（可带 OTP）登录，成功返回 JWT token
        g.MapPost("/login", async (LoginReq req, IAuthService auth) =>
        {
            var r = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            // 以 Go 约定的 { code, message, data:{token} } 形式返回，即使失败也返回 HTTP 200
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        // POST /api/auth/login/2fa — 已开启两步验证时的登录入口（与 /login 相同逻辑）
        g.MapPost("/login/2fa", async (Login2FaReq req, IAuthService auth) =>
        {
            var r = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        // POST /api/auth/login/hash — 密码哈希登录（兼容旧客户端）
        g.MapPost("/login/hash", async (LoginReq req, IAuthService auth) =>
        {
            var r = await auth.LoginHashAsync(req.Username, req.Password, req.OtpCode);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        // POST /api/auth/login/ldap — LDAP 登录. Same body as /login/hash.
        // LDAP 登录，请求体与 /login/hash 相同；前端在启用 LDAP 模式时切换此入口。
        // 服务在未配置 LDAP 后端时返回 501。
        g.MapPost("/login/ldap", async (LoginReq req, IAuthService auth) =>
        {
            var r = await auth.LoginLdapAsync(req.Username, req.Password);
            return r.Code == 200
                ? Results.Ok(new { code = 200, message = "success", data = new { token = r.Data } })
                : Results.Ok(new { code = r.Code, message = r.Message, data = (object?)null });
        }).AllowAnonymous();

        // Go registers GET /auth/logout. Accept GET (Go/frontend) and POST (older .NET clients).
        // GET/POST /api/auth/logout — 使当前 token 失效；同时兼容 Go 前端(GET)与旧 .NET 客户端(POST)，需登录。
        g.MapMethods("/logout", new[] { "GET", "POST" }, async (HttpContext ctx, IAuthService auth) =>
        {
            var token = ExtractToken(ctx);
            if (token is null) return Results.Ok(Resp.Fail("no token", RespCodes.Unauthorized));
            await auth.LogoutAsync(token);
            return Results.Ok(Resp.Ok());
        }).RequireAuthorization();

        // GET /api/auth/me — 返回当前登录用户公开信息（需登录）
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
            // 生成 otpauth:// 协议地址，供前端渲染二维码
            var resp = new Generate2FaResp
            {
                Secret = r.Data!,
                QrUri = $"otpauth://totp/OpenList:{user.Identity?.Name}?secret={r.Data}&issuer=OpenList",
            };
            return Results.Ok(Resp<Generate2FaResp>.Ok(resp));
        }).RequireAuthorization();

        // POST /api/auth/2fa/verify — 校验动态口令码（需登录）
        g.MapPost("/2fa/verify", async (ClaimsPrincipal user, Verify2FaReq req, IAuthService auth) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Ok(Resp.Fail("unauthorized", RespCodes.Unauthorized));
            var r = await auth.Verify2FaAsync(id.Value, req.Code);
            return Results.Ok(r);
        }).RequireAuthorization();

        // POST /api/auth/hash_password — 计算密码哈希（仅管理员，前端用于预生成哈希）
        g.MapPost("/hash_password", (HashPasswordReq req) =>
            Results.Ok(Resp<HashPasswordResp>.Ok(new HashPasswordResp { Hash = UserService.HashPassword(req.Password) })))
         .RequireAuthorization("admin");

        // --- SSO 登录 ---
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
        // GET /api/auth/sso — SSO 登录入口，按 ?method= 分发
        g.MapGet("/sso", async (string? method, HttpContext ctx, ISsoService sso) =>
        {
            if (string.IsNullOrEmpty(method))
                return Results.Ok(Resp.Fail("no method provided", RespCodes.BadRequest));
            // 依据当前请求上下文构造回调地址，用于向 IdP 申请授权 URL
            var redirectUri = BuildSsoCallbackUrl(ctx, method);
            var r = await sso.GetAuthorizeUrlAsync(method, redirectUri);
            if (r.Code != RespCodes.Ok) return Results.Ok(r);
            return Results.Redirect(r.Data!);
        }).AllowAnonymous();

        // GET /api/auth/sso_callback — IdP 回调，换取 token/ssoId 并回传前端
        g.MapGet("/sso_callback", async (string? method, string? code, string? state, HttpContext ctx, ISsoService sso, ISettingService settings) =>
        {
            // 兼容模式：method 以 URL 路径段而非查询参数编码
            var effectiveMethod = method ?? LastUrlSegment(ctx.Request.Path.Value);
            if (string.IsNullOrEmpty(effectiveMethod))
                return Results.Ok(Resp.Fail("no method provided", RespCodes.BadRequest));
            var redirectUri = BuildSsoCallbackUrl(ctx, effectiveMethod);
            var r = await sso.HandleCallbackAsync(effectiveMethod, code ?? "", state, redirectUri);
            if (r.Code != RespCodes.Ok) return Results.Ok(r);
            // 返回一段 HTML，通过 postMessage 把结果交回弹窗 opener（目标源受限）
            return Results.Text(RenderSsoCallbackHtml(r.Data!, await ResolveSsoTargetOriginAsync(ctx, settings)),
                "text/html; charset=utf-8");
        }).AllowAnonymous();

        // Aliases matching Go's dedicated callback routes (compatibility mode paths).
        g.MapGet("/get_sso_id", (string? code, string? state, HttpContext ctx, ISsoService sso, ISettingService settings) =>
            HandleCompatCallbackAsync("get_sso_id", code, state, ctx, sso, settings)).AllowAnonymous();
        g.MapGet("/sso_get_token", (string? code, string? state, HttpContext ctx, ISsoService sso, ISettingService settings) =>
            HandleCompatCallbackAsync("sso_get_token", code, state, ctx, sso, settings)).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> HandleCompatCallbackAsync(string method, string? code, string? state, HttpContext ctx, ISsoService sso, ISettingService settings)
    {
        // 兼容模式下 IdP 直接回调 /api/auth/get_sso_id（不携带 ?method=）。
        // 传给 IdP 的 redirect_uri 正是基于此路径构造的，这里重建以便 token 交换时校验。
        var redirectUri = $"{GetApiBaseUrl(ctx)}/api/auth/{method}";
        var r = await sso.HandleCallbackAsync(method, code ?? "", state, redirectUri);
        if (r.Code != RespCodes.Ok) return Results.Ok(r);
        return Results.Text(RenderSsoCallbackHtml(r.Data!, await ResolveSsoTargetOriginAsync(ctx, settings)),
            "text/html; charset=utf-8");
    }

    /// <summary>
    /// 解析 SSO 回调 postMessage 的 targetOrigin。镜像 Go <c>server/handles/ssologin.go</c> 的
    /// <c>ssoTargetOrigin</c>：优先采用操作员配置的 <c>sso_postmessage_origin</c>
    /// （须为纯 scheme+host 的 http/https 源），否则退回 API 自身源，
    /// 都拿不到时退回 <c>"/"</c>（仅允许同源 opener 接收）。
    /// </summary>
    private static async Task<string> ResolveSsoTargetOriginAsync(HttpContext ctx, ISettingService settings)
    {
        try
        {
            var item = await settings.GetAsync("sso_postmessage_origin", ctx.RequestAborted);
            return SsoTargetOrigin(ctx, item.Code == 200 ? item.Data?.Value : null);
        }
        catch
        {
            return SsoTargetOrigin(ctx, null);
        }
    }

    /// <summary>校验并归一化 SSO 回调目标源；非法配置回退到 API 源或 "/"。</summary>
    internal static string SsoTargetOrigin(HttpContext ctx, string? configured)
    {
        if (!string.IsNullOrEmpty(configured)
            && Uri.TryCreate(configured, UriKind.Absolute, out var cu)
            && (cu.Scheme == Uri.UriSchemeHttp || cu.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(cu.Host)
            && string.IsNullOrEmpty(cu.UserInfo)
            && cu.AbsolutePath is "" or "/"
            && string.IsNullOrEmpty(cu.Query)
            && string.IsNullOrEmpty(cu.Fragment))
        {
            return $"{cu.Scheme}://{cu.Authority}";
        }

        if (Uri.TryCreate(GetApiBaseUrl(ctx), UriKind.Absolute, out var au)
            && !string.IsNullOrEmpty(au.Scheme)
            && !string.IsNullOrEmpty(au.Host))
        {
            return $"{au.Scheme}://{au.Authority}";
        }

        return "/";
    }

    private static string BuildSsoCallbackUrl(HttpContext ctx, string method)
    {
        // 非兼容模式：统一回调到 /api/auth/sso_callback?method=...
        return $"{GetApiBaseUrl(ctx)}/api/auth/sso_callback?method={Uri.EscapeDataString(method)}";
    }

    private static string GetApiBaseUrl(HttpContext ctx)
    {
        var req = ctx.Request;
        // 反向代理场景下优先采用 X-Forwarded-* 头推断协议与主机
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
    /// 输出与 Go 服务一致的 HTML：通过 postMessage 把 { sso_id } 或 { token }
    /// 交给窗口 opener 后自动关闭弹窗，供前端 Profile.tsx 监听。
    /// <paramref name="targetOrigin"/> 由 <see cref="SsoTargetOrigin"/> 钉死，
    /// 不使用 <c>"*"</c>，避免任意页面开弹窗窃取登录令牌。
    /// </summary>
    internal static string RenderSsoCallbackHtml(SsoCallbackResult r, string targetOrigin)
    {
        var payload = r.Token is not null
            ? $"{{\"token\":\"{EscapeJs(r.Token)}\"}}"
            : $"{{\"sso_id\":\"{EscapeJs(r.SsoId ?? string.Empty)}\"}}";
        return $@"<!DOCTYPE html><head></head><body><script>
window.opener.postMessage({payload}, ""{EscapeJs(targetOrigin)}"");
window.close();
</script></body>";
    }

    private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("<", "\\u003c");

    internal static string? ExtractToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth)) return null;
        // 前端发送的 token 不含 "Bearer " 前缀，故两种形态均接受
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : auth;
    }
}

/// <summary>
/// WebAuthn（通行密钥/Passkey）端点。对应 OpenList Go
/// <c>server/handles/webauthn.go</c> + 路由中的 authn 组。
/// 提供登录/注册/凭据管理：/webauthn_begin_login、/webauthn_finish_login、
/// /webauthn_begin_registration、/webauthn_finish_registration、
/// /getcredentials、/delete_authn。登录相关匿名，其余需登录。
/// 浏览器会在 begin 与 finish 两步间原样回传 Session 头（base64 编码的 JSON 会话）。
/// </summary>
public static class AuthnEndpoints
{
    /// <summary>
    /// 注册 <c>/api/authn</c> WebAuthn 路由组。begin 阶段 GET、finish 阶段 POST，
    /// 会话经 <c>Session</c> 请求头携带；登录匿名，注册与凭据管理需鉴权。
    /// </summary>
    public static IEndpointRouteBuilder MapAuthnApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/authn").WithTags("Authn");

        // --- 登录（匿名） ---
        // username 在"可发现的(条件中介)"登录场景下可为空
        g.MapGet("/webauthn_begin_login", async (string? username, IWebAuthnService svc) =>
            Results.Ok(await svc.BeginLoginAsync(username)))
         .AllowAnonymous();

        // POST /api/authn/webauthn_finish_login — 校验收到的断言并返回 JWT（匿名）
        g.MapPost("/webauthn_finish_login", async (HttpContext ctx, IWebAuthnService svc) =>
        {
            var username = ctx.Request.Query["username"].FirstOrDefault();
            var session = ctx.Request.Headers["session"].ToString();
            var body = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            return Results.Ok(await svc.FinishLoginAsync(username, session, body));
        })
         .AllowAnonymous();

        // --- 注册（需登录） ---
        g.MapGet("/webauthn_begin_registration", async (ClaimsPrincipal user, IWebAuthnService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Unauthorized();
            return Results.Ok(await svc.BeginRegistrationAsync(id.Value));
        })
         .RequireAuthorization();

        // POST /api/authn/webauthn_finish_registration — 保存新凭据（需登录）
        g.MapPost("/webauthn_finish_registration", async (HttpContext ctx, ClaimsPrincipal user, IWebAuthnService svc) =>
        {
            var id = user.UserId();
            if (id is null) return Results.Unauthorized();
            var session = ctx.Request.Headers["session"].ToString();
            var body = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            return Results.Ok(await svc.FinishRegistrationAsync(id.Value, session, body));
        })
         .RequireAuthorization();

        // --- 凭据管理（需登录） ---
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

/// <summary>删除 WebAuthn 凭据的请求体（<c>POST /api/authn/delete_authn</c>）。</summary>
public sealed class DeleteAuthnReq
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// 从 <see cref="ClaimsPrincipal"/>（JWT 声明）中提取用户身份与角色的扩展方法。
/// </summary>
public static class AuthClaimsExtensions
{
    /// <summary>从声明中解析用户 Id（ClaimTypes.NameIdentifier），解析失败返回 null。</summary>
    public static long? UserId(this ClaimsPrincipal principal)
    {
        var v = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(v, out var id) ? id : null;
    }

    /// <summary>从角色声明解析用户角色，非法值回退为 Guest。</summary>
    public static UserRole UserRole(this ClaimsPrincipal principal)
    {
        var v = principal.FindFirst(ClaimTypes.Role)?.Value;
        return int.TryParse(v, out var r) ? (UserRole)r : Core.Models.UserRole.Guest;
    }

    /// <summary>是否为管理员角色。</summary>
    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.UserRole() == Core.Models.UserRole.Admin;

    /// <summary>
    /// 把声明映射为最小 <see cref="User"/>，供服务层做权限校验。
    /// <para>
    /// 除身份三件套外还带出 <see cref="User.BasePath"/> 与 <see cref="User.Permission"/>：
    /// 它们由 <c>JwtService.IssueToken</c> 签进 token，是目录隔离（BasePath）与权限位判定的唯一来源。
    /// 之前没带出来，导致服务层看到的用户永远是 BasePath=null、Permission=0 ——
    /// 表现为「普通用户被当成零权限、且 BasePath 约束完全不生效」。
    /// </para>
    /// <para>
    /// 匿名请求（虚拟 Guest，<c>Id=0</c>）没有这两个 claim：BasePath 退回 null（等价于根目录、不约束），
    /// Permission 退回 0。改造前签发的旧 token 同理，行为与改造前一致，因此不存在兼容性回归。
    /// </para>
    /// </summary>
    public static User ToUser(this ClaimsPrincipal principal)
    {
        var id = principal.UserId() ?? 0;
        var basePath = principal.FindFirst(OpenListClaims.BasePath)?.Value;
        var permission = principal.FindFirst(OpenListClaims.Permission)?.Value;
        return new User
        {
            Id = id,
            Username = principal.Identity?.Name ?? string.Empty,
            Role = principal.UserRole(),
            BasePath = string.IsNullOrEmpty(basePath) ? null : basePath,
            Permission = int.TryParse(permission, out var p) ? p : 0,
        };
    }
}
