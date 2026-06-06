using System.Security.Claims;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        // Public endpoints (no auth required)
        group.MapPost("login", async (IAuthService auth, LoginReq req) =>
        {
            var token = await auth.LoginAsync(req.Username, req.Password, req.OtpCode);
            if (token == "2FA_REQUIRED")
                return Results.Json(Resp<LoginResp>.Error(402, "2FA code required"), statusCode: 200);
            return token == null
                ? Results.Json(Resp<LoginResp>.Error(401, "login failed"), statusCode: 200)
                : Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        });

        group.MapPost("login/hash", async (IAuthService auth, LoginHashReq req) =>
        {
            var token = await auth.LoginHashAsync(req.Username, req.PasswordHash, req.OtpCode);
            if (token == "2FA_REQUIRED")
                return Results.Json(Resp<LoginResp>.Error(402, "2FA code required"), statusCode: 200);
            return token == null
                ? Results.Json(Resp<LoginResp>.Error(401, "login failed"), statusCode: 200)
                : Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        });

        group.MapPost("login/ldap", async (IAuthService auth, LoginReq req) =>
        {
            var token = await auth.LoginLdapAsync(req.Username, req.Password);
            return token == null
                ? Results.Json(Resp<LoginResp>.Error(401, "ldap login failed"), statusCode: 200)
                : Results.Json(new Resp<LoginResp>(new LoginResp { Token = token }));
        });

        // SSO endpoints (public)
        group.MapGet("sso", (IConfiguration config, HttpContext ctx) =>
        {
            var enabled = config["SsoLoginEnabled"] == "true";
            if (!enabled) return Results.Json(Resp<object>.Error(403, "SSO not enabled"));

            var method = ctx.Request.Query["method"].ToString();
            if (string.IsNullOrEmpty(method)) return Results.Json(Resp<object>.Error(400, "no method provided"));

            var clientId = config["SsoClientId"] ?? "";
            var platform = config["SsoLoginPlatform"] ?? "";
            var endpoint = config["SsoEndpointName"] ?? "";
            var compatibility = config["SsoCompatibilityMode"] == "true";

            var redirectUri = compatibility
                ? $"{GetApiUrl(ctx)}/api/auth/{method}"
                : $"{GetApiUrl(ctx)}/api/auth/sso_callback?method={method}";

            var queryParams = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId
            };

            var authUrl = platform switch
            {
                "Github" => BuildOAuthUrl("https://github.com/login/oauth/authorize", queryParams, "read:user"),
                "Microsoft" => BuildOAuthUrl("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", queryParams, "user.read", ("response_mode", "query")),
                "Google" => BuildOAuthUrl("https://accounts.google.com/o/oauth2/v2/auth", queryParams, "https://www.googleapis.com/auth/userinfo.profile"),
                "Dingtalk" => BuildOAuthUrl("https://login.dingtalk.com/oauth2/auth", queryParams, "openid", ("prompt", "consent")),
                "Casdoor" => BuildOAuthUrl($"{endpoint.TrimEnd('/')}/login/oauth/authorize", queryParams, "profile", ("state", endpoint)),
                _ => null
            };

            if (authUrl == null) return Results.Json(Resp<object>.Error(400, $"invalid platform: {platform}"));
            return Results.Redirect(authUrl);
        });

        group.MapGet("sso_callback", async (IAuthService auth, IUserService userService, IConfiguration config, HttpContext ctx) =>
        {
            var result = await HandleSsoCallback(auth, userService, config, ctx);
            return result;
        });

        group.MapGet("get_sso_id", async (IAuthService auth, IUserService userService, IConfiguration config, HttpContext ctx) =>
        {
            var result = await HandleSsoCallback(auth, userService, config, ctx);
            return result;
        });

        group.MapGet("sso_get_token", async (IAuthService auth, IUserService userService, IConfiguration config, HttpContext ctx) =>
        {
            var result = await HandleSsoCallback(auth, userService, config, ctx);
            return result;
        });

        // Authenticated endpoints
        group.MapGet("logout", async (IAuthService auth, HttpContext ctx) =>
        {
            var token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
            if (!string.IsNullOrEmpty(token))
                await auth.InvalidateTokenAsync(token);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        group.MapPost("2fa/generate", async (IAuthService auth, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<TwoFAResp>.Error(401, "unauthorized"));
            var (qr, secret) = await auth.Generate2FAAsync(userId.Value);
            return Results.Json(new Resp<TwoFAResp>(new TwoFAResp { QR = qr, Secret = secret }));
        }).RequireAuthorization();

        group.MapPost("2fa/verify", async (IAuthService auth, Verify2FAReq req, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));
            var ok = await auth.Verify2FAAsync(userId.Value, req.Code, req.Secret);
            return ok
                ? Results.Json(new Resp<object>(null))
                : Results.Json(Resp<object>.Error(400, "verification failed"));
        }).RequireAuthorization();

        // WebAuthn endpoints (Go: /api/authn/*)
        var authn = endpoints.MapGroup("/api/authn");
        var webAuthnService = endpoints.ServiceProvider.GetRequiredService<WebAuthnService>();

        // Public: login endpoints
        authn.MapGet("webauthn_begin_login", async (string? username, CancellationToken ct) =>
        {
            var options = await webAuthnService.BeginLoginAsync(username, ct);
            if (!string.IsNullOrEmpty(username))
                webAuthnService.CacheAssertionOptions(username, options);
            return Results.Json(new Resp<object>(options));
        });

        authn.MapPost("webauthn_finish_login", async (HttpContext ctx, IUserService userService, IAuthService authService, string? username, CancellationToken ct) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<AuthenticatorAssertionRawResponse>(ct);
            if (body == null) return Results.Json(Resp<object>.Error(400, "invalid request"));
            if (string.IsNullOrEmpty(username)) return Results.Json(Resp<object>.Error(400, "username required"));

            var userId = await webAuthnService.FinishLoginAsync(username, body, ct);
            if (userId == null) return Results.Json(Resp<object>.Error(400, "authentication failed"));

            var user = await userService.GetUserByIdAsync(userId.Value, ct);
            if (user == null) return Results.Json(Resp<object>.Error(400, "user not found"));

            var token = authService.GenerateToken(user);
            return Results.Json(new Resp<object>(new { Token = token }));
        });

        // Authenticated: registration/management
        authn.MapGet("webauthn_begin_registration", async (ClaimsPrincipal user, string? credentialName, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));

            var username = user.FindFirst(ClaimTypes.Name)?.Value ?? "";
            var options = await webAuthnService.BeginRegistrationAsync(userId.Value, username, credentialName, ct);
            webAuthnService.CacheRegistrationOptions(userId.Value, options);
            return Results.Json(new Resp<object>(options));
        }).RequireAuthorization();

        authn.MapPost("webauthn_finish_registration", async (HttpContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));

            var body = await ctx.Request.ReadFromJsonAsync<AuthenticatorAttestationRawResponse>(ct);
            if (body == null) return Results.Json(Resp<object>.Error(400, "invalid request"));

            var credentialName = ctx.Request.Query["name"].ToString();
            var credential = await webAuthnService.FinishRegistrationAsync(userId.Value, credentialName, body, ct);
            return Results.Json(new Resp<object>(credential));
        }).RequireAuthorization();

        authn.MapPost("delete_authn", async (ClaimsPrincipal user, int credentialId, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));

            await webAuthnService.DeleteCredentialAsync(credentialId, userId.Value, ct);
            return Results.Json(new Resp<object>(null));
        }).RequireAuthorization();

        authn.MapGet("getcredentials", async (ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId == null) return Results.Json(Resp<object>.Error(401, "unauthorized"));

            var credentials = await webAuthnService.GetCredentialsAsync(userId.Value, ct);
            return Results.Json(new Resp<object>(credentials));
        }).RequireAuthorization();

        return endpoints;
    }

    private static uint? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && uint.TryParse(claim.Value, out var id) ? id : null;
    }

    private static string GetApiUrl(HttpContext ctx)
    {
        var scheme = ctx.Request.Scheme;
        var host = ctx.Request.Host.Value;
        return $"{scheme}://{host}";
    }

    private static string BuildOAuthUrl(string baseUrl, Dictionary<string, string> queryParams, string scope, params (string Key, string Value)[] extraParams)
    {
        queryParams["scope"] = scope;
        foreach (var (key, value) in extraParams)
            queryParams[key] = value;
        return baseUrl + "?" + string.Join("&", queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    }

    private static async Task<IResult> HandleSsoCallback(IAuthService authService, IUserService userService, IConfiguration config, HttpContext ctx)
    {
        var enabled = config["SsoLoginEnabled"] == "true";
        if (!enabled) return Results.Json(Resp<object>.Error(403, "SSO not enabled"));

        var platform = config["SsoLoginPlatform"] ?? "";
        var clientId = config["SsoClientId"] ?? "";
        var clientSecret = config["SsoClientSecret"] ?? "";
        var endpoint = config["SsoEndpointName"] ?? "";
        var compatibility = config["SsoCompatibilityMode"] == "true";

        var method = ctx.Request.Query["method"].ToString();
        if (compatibility && string.IsNullOrEmpty(method))
            method = ctx.Request.Path.Value?.Split('/').LastOrDefault() ?? "";

        if (method != "get_sso_id" && method != "sso_get_token")
            return Results.Json(Resp<object>.Error(400, "invalid method"));

        // Get the authorization code based on platform
        var codeParam = platform == "Dingtalk" ? "authCode" : "code";
        var code = ctx.Request.Query[codeParam].ToString();
        if (string.IsNullOrEmpty(code)) return Results.Json(Resp<object>.Error(400, "no code provided"));

        var redirectUri = compatibility
            ? $"{GetApiUrl(ctx)}/api/auth/{method}"
            : $"{GetApiUrl(ctx)}/api/auth/sso_callback?method={method}";

        // Exchange code for access token and get user info
        var (userId, username) = platform switch
        {
            "Github" => await ExchangeGithubToken(clientId, clientSecret, code, redirectUri),
            "Microsoft" => await ExchangeMicrosoftToken(clientId, clientSecret, code, redirectUri),
            "Google" => await ExchangeGoogleToken(clientId, clientSecret, code, redirectUri),
            "Dingtalk" => await ExchangeDingtalkToken(clientId, clientSecret, code),
            "Casdoor" => await ExchangeCasdoorToken(clientId, clientSecret, code, redirectUri, endpoint),
            _ => (null, null)
        };

        if (string.IsNullOrEmpty(userId))
            return Results.Json(Resp<object>.Error(400, "failed to get user info from SSO provider"));

        if (method == "get_sso_id")
        {
            if (compatibility)
                return Results.Redirect($"{GetApiUrl(ctx)}/@manage?sso_id={userId}");
            return Results.Text($"<!DOCTYPE html><head></head><body><script>window.opener.postMessage({{\"sso_id\": \"{userId}\"}}, \"*\");window.close();</script></body>", "text/html");
        }

        // sso_get_token - find or auto-register user
        var user = await userService.GetUserBySsoIdAsync(userId);
        if (user == null)
        {
            var autoRegister = config["SsoAutoRegister"] == "true";
            if (!autoRegister || string.IsNullOrEmpty(username))
                return Results.Json(Resp<object>.Error(400, "user not found and auto-register is disabled"));

            user = new User
            {
                Username = username,
                PwdHash = OpenList.Infrastructure.Services.UserService.StaticHash(Guid.NewGuid().ToString("N")),
                Permission = int.TryParse(config["SsoDefaultPermission"], out var perm) ? perm : 0,
                BasePath = config["SsoDefaultDir"] ?? "/",
                SsoID = userId
            };
            await userService.CreateUserAsync(user);
        }

        var token = authService.GenerateToken(user);
        if (compatibility)
            return Results.Redirect($"{GetApiUrl(ctx)}/@login?token={token}");

        return Results.Text($"<!DOCTYPE html><head></head><body><script>window.opener.postMessage({{\"token\": \"{token}\"}}, \"*\");window.close();</script></body>", "text/html");
    }

    private static async Task<(string? UserId, string? Username)> ExchangeGithubToken(string clientId, string clientSecret, string code, string redirectUri)
    {
        using var http = new HttpClient();
        var tokenResp = await http.PostAsync("https://github.com/login/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code, ["redirect_uri"] = redirectUri
        }));
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        var tokenJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tokenBody);
        var accessToken = tokenJson?.GetValueOrDefault("access_token").GetString();

        if (string.IsNullOrEmpty(accessToken)) return (null, null);

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenList");
        var userResp = await http.GetStringAsync("https://api.github.com/user");
        var userJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(userResp);
        return (userJson?.GetValueOrDefault("id").ToString(), userJson?.GetValueOrDefault("login").GetString());
    }

    private static async Task<(string? UserId, string? Username)> ExchangeMicrosoftToken(string clientId, string clientSecret, string code, string redirectUri)
    {
        using var http = new HttpClient();
        var tokenResp = await http.PostAsync("https://login.microsoftonline.com/common/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code,
            ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code", ["scope"] = "user.read"
        }));
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        var tokenJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tokenBody);
        var accessToken = tokenJson?.GetValueOrDefault("access_token").GetString();

        if (string.IsNullOrEmpty(accessToken)) return (null, null);

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var userResp = await http.GetStringAsync("https://graph.microsoft.com/v1.0/me");
        var userJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(userResp);
        return (userJson?.GetValueOrDefault("id").GetString(), userJson?.GetValueOrDefault("displayName").GetString());
    }

    private static async Task<(string? UserId, string? Username)> ExchangeGoogleToken(string clientId, string clientSecret, string code, string redirectUri)
    {
        using var http = new HttpClient();
        var tokenResp = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code,
            ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code"
        }));
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        var tokenJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tokenBody);
        var accessToken = tokenJson?.GetValueOrDefault("access_token").GetString();

        if (string.IsNullOrEmpty(accessToken)) return (null, null);

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var userResp = await http.GetStringAsync("https://www.googleapis.com/oauth2/v1/userinfo");
        var userJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(userResp);
        return (userJson?.GetValueOrDefault("id").GetString(), userJson?.GetValueOrDefault("name").GetString());
    }

    private static async Task<(string? UserId, string? Username)> ExchangeDingtalkToken(string clientId, string clientSecret, string code)
    {
        using var http = new HttpClient();
        var tokenResp = await http.PostAsync("https://api.dingtalk.com/v1.0/oauth2/userAccessToken", new StringContent(
            JsonSerializer.Serialize(new { clientId, clientSecret, code, grantType = "authorization_code" }),
            System.Text.Encoding.UTF8, "application/json"));
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        var tokenJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tokenBody);
        var accessToken = tokenJson?.GetValueOrDefault("accessToken").GetString();

        if (string.IsNullOrEmpty(accessToken)) return (null, null);

        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.dingtalk.com/v1.0/contact/users/me");
        req.Headers.Add("x-acs-dingtalk-access-token", accessToken);
        var userResp = await http.SendAsync(req);
        var userBody = await userResp.Content.ReadAsStringAsync();
        var userJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(userBody);
        return (userJson?.GetValueOrDefault("unionId").GetString(), userJson?.GetValueOrDefault("nick").GetString());
    }

    private static async Task<(string? UserId, string? Username)> ExchangeCasdoorToken(string clientId, string clientSecret, string code, string redirectUri, string endpoint)
    {
        using var http = new HttpClient();
        var ep = endpoint.TrimEnd('/');
        var tokenResp = await http.PostAsync($"{ep}/api/login/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code,
            ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code", ["scope"] = "profile"
        }));
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        var tokenJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tokenBody);
        var accessToken = tokenJson?.GetValueOrDefault("access_token").GetString();

        if (string.IsNullOrEmpty(accessToken)) return (null, null);

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var userResp = await http.GetStringAsync($"{ep}/api/userinfo");
        var userJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(userResp);
        return (userJson?.GetValueOrDefault("sub").GetString(), userJson?.GetValueOrDefault("preferred_username").GetString());
    }
}
