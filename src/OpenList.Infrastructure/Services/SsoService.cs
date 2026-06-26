using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// SSO orchestration using OAuth2 authorization-code flow. Reads all configuration
/// from the settings store (group 7 — see <c>DbInitializer</c>) so admins can tune
/// it from the frontend without restarting. Mirrors OpenList Go
/// <c>server/handles/ssologin.go</c>.
///
/// Supported platforms:
/// <list type="bullet">
/// <item><c>Casdoor</c> — self-hosted IdP at <c>SSOEndpointName</c>.</item>
/// <item><c>OIDC</c> — generic OpenID Connect (discovery via <c>.well-known/openid-configuration</c>).</item>
/// <item><c>Github</c>, <c>Google</c>, <c>Microsoft</c>, <c>Dingtalk</c> — stubbed (return 501).</item>
/// </list>
/// </summary>
public sealed class SsoService : ISsoService
{
    private readonly OpenListDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IUserService _users;
    private readonly IAuthService _auth;

    public SsoService(OpenListDbContext db, IHttpClientFactory http, IUserService users, IAuthService auth)
    {
        _db = db;
        _http = http;
        _users = users;
        _auth = auth;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        return s.Enabled && !string.IsNullOrWhiteSpace(s.ClientId);
    }

    public async Task<Resp<string>> GetAuthorizeUrlAsync(string method, string redirectUri, CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        if (!s.Enabled) return Resp<string>.Fail("sso login is not enabled", RespCodes.Forbidden);
        if (string.IsNullOrWhiteSpace(s.ClientId)) return Resp<string>.Fail("sso client_id not configured", RespCodes.BadRequest);
        if (method is not ("get_sso_id" or "sso_get_token"))
            return Resp<string>.Fail("invalid method", RespCodes.BadRequest);

        // For non-compatibility mode the callback is always /api/auth/sso_callback?method=...
        // The caller passes the fully-built redirectUri so we don't need to know the host.
        var scope = s.Platform switch
        {
            "Github" => "read:user",
            "Microsoft" => "user.read",
            "Google" => "https://www.googleapis.com/auth/userinfo.profile",
            "Dingtalk" => "openid",
            "Casdoor" or "OIDC" => string.IsNullOrEmpty(s.ExtraScopes) ? "profile" : s.ExtraScopes,
            _ => "profile",
        };

        var authorizeUrl = s.Platform switch
        {
            "Github" => "https://github.com/login/oauth/authorize",
            "Microsoft" => "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            "Google" => "https://accounts.google.com/o/oauth2/v2/auth",
            "Dingtalk" => "https://login.dingtalk.com/oauth2/auth",
            "Casdoor" => s.Endpoint.TrimEnd('/') + "/login/oauth/authorize",
            "OIDC" => await DiscoverAuthorizeEndpointAsync(s.Endpoint, ct),
            _ => null,
        };
        if (authorizeUrl is null) return Resp<string>.Fail($"unsupported sso platform: {s.Platform}", RespCodes.BadRequest);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["client_id"] = s.ClientId,
            ["scope"] = scope,
            ["state"] = method,
        };
        if (s.Platform == "Microsoft") query["response_mode"] = "query";

        var ub = new UriBuilder(authorizeUrl) { Query = BuildQueryString(query) };
        return Resp<string>.Ok(ub.Uri.AbsoluteUri);
    }

    public async Task<Resp<SsoCallbackResult>> HandleCallbackAsync(string method, string code, string? state, string redirectUri, CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        if (!s.Enabled) return Resp<SsoCallbackResult>.Fail("sso login is not enabled", RespCodes.Forbidden);
        if (method is not ("get_sso_id" or "sso_get_token"))
            return Resp<SsoCallbackResult>.Fail("invalid method", RespCodes.BadRequest);
        if (string.IsNullOrWhiteSpace(code))
            return Resp<SsoCallbackResult>.Fail("no code provided", RespCodes.BadRequest);

        Task<Resp<SsoUserProfile>> profileTask = s.Platform switch
        {
            "Casdoor" => ExchangeCasdoorAsync(s, code, redirectUri, ct),
            "OIDC" => ExchangeOidcAsync(s, code, redirectUri, ct),
            "Github" => ExchangeGithubAsync(s, code, redirectUri, ct),
            "Google" => ExchangeGoogleAsync(s, code, redirectUri, ct),
            "Microsoft" => ExchangeMicrosoftAsync(s, code, redirectUri, ct),
            "Dingtalk" => ExchangeDingtalkAsync(s, code, redirectUri, ct),
            _ => Task.FromResult(Resp<SsoUserProfile>.Fail($"unsupported platform: {s.Platform}", RespCodes.BadRequest)),
        };
        var profile = await profileTask;
        if (!profile.IsSuccess() || string.IsNullOrEmpty(profile.Data!.Id))
            return Resp<SsoCallbackResult>.Fail(profile.Message, profile.Code);

        var userId = profile.Data.Id;

        if (method == "get_sso_id")
            return Resp<SsoCallbackResult>.Ok(new SsoCallbackResult { SsoId = userId });

        // sso_get_token: look up the local user, auto-register if allowed, issue JWT.
        var lookup = await _users.GetBySsoIdAsync(userId, ct);
        User u;
        if (lookup.IsSuccess())
        {
            u = lookup.Data!;
        }
        else
        {
            if (!s.AutoRegister)
                return Resp<SsoCallbackResult>.Fail("sso user not registered", RespCodes.NotFound);
            var username = string.IsNullOrEmpty(profile.Data.Username) ? userId : profile.Data.Username;
            var create = await _users.CreateAsync(new CreateUserReq
            {
                Username = username,
                Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Role = UserRole.General,
                Permission = s.DefaultPermission,
                BasePath = s.DefaultDir,
            }, ct);
            if (!create.IsSuccess())
            {
                // Username collision — disambiguate with the SSO id suffix (matches Go behaviour).
                create = await _users.CreateAsync(new CreateUserReq
                {
                    Username = username + "_" + userId,
                    Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    Role = UserRole.General,
                    Permission = s.DefaultPermission,
                    BasePath = s.DefaultDir,
                }, ct);
                if (!create.IsSuccess()) return Resp<SsoCallbackResult>.Fail(create.Message, create.Code);
            }
            var fetched = await _users.GetByNameAsync(username, ct);
            if (!fetched.IsSuccess())
            {
                fetched = await _users.GetByNameAsync(username + "_" + userId, ct);
                if (!fetched.IsSuccess()) return Resp<SsoCallbackResult>.Fail("sso user provisioning failed", RespCodes.InternalError);
            }
            u = fetched.Data!;
            // Stamp the SSO id onto the freshly-created account.
            await _users.UpdateAsync(u.Id, new UpdateUserReq { Id = u.Id, SsoID = userId }, ct);
        }

        if (u.Disabled) return Resp<SsoCallbackResult>.Fail("user disabled", RespCodes.Forbidden);
        var token = _auth.IssueToken(u);
        return Resp<SsoCallbackResult>.Ok(new SsoCallbackResult { Token = token });
    }

    // --- per-platform token exchange + userinfo fetch ---

    private async Task<Resp<SsoUserProfile>> ExchangeCasdoorAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
    {
        var tokenUrl = s.Endpoint.TrimEnd('/') + "/api/login/oauth/access_token";
        var userUrl = s.Endpoint.TrimEnd('/') + "/api/userinfo";
        return await ExchangeOAuth2Async(tokenUrl, userUrl, s.ClientId, s.ClientSecret, code, redirectUri, "sub", "preferred_username", "profile", ct);
    }

    private async Task<Resp<SsoUserProfile>> ExchangeOidcAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
    {
        // For OIDC we discover endpoints, exchange for an id_token, and extract the
        // configured username key from the JWT payload (no signature verification —
        // the IdP is already trusted via TLS, matching Go's lenient parseJWT).
        var cfg = await DiscoverOidcConfigAsync(s.Endpoint, ct);
        if (cfg is null) return Resp<SsoUserProfile>.Fail("oidc discovery failed", RespCodes.BadRequest);

        var token = await PostFormAsync(cfg.TokenEndpoint, new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = s.ClientId,
            ["client_secret"] = s.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.IsNullOrEmpty(s.ExtraScopes) ? "openid profile" : "openid profile " + s.ExtraScopes,
        }, ct);
        if (!token.IsSuccess()) return Resp<SsoUserProfile>.Fail(token.Message, token.Code);

        var idToken = token.Data!.TryGetValue("id_token", out var t) ? t : null;
        if (string.IsNullOrEmpty(idToken))
            return Resp<SsoUserProfile>.Fail("no id_token in oidc response", RespCodes.BadRequest);

        // Decode the JWT payload (no verification — see comment above).
        var parts = idToken.Split('.');
        if (parts.Length < 2) return Resp<SsoUserProfile>.Fail("malformed id_token", RespCodes.BadRequest);
        var payload = Base64UrlDecode(parts[1]);
        using var doc = JsonDocument.Parse(payload);
        var usernameKey = string.IsNullOrEmpty(s.OidcUsernameKey) ? "name" : s.OidcUsernameKey;
        var sub = doc.RootElement.TryGetProperty("sub", out var subEl) && subEl.ValueKind == JsonValueKind.String ? subEl.GetString() : null;
        var name = doc.RootElement.TryGetProperty(usernameKey, out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : null;
        if (string.IsNullOrEmpty(sub)) return Resp<SsoUserProfile>.Fail("oidc: no sub claim", RespCodes.BadRequest);
        return Resp<SsoUserProfile>.Ok(new SsoUserProfile { Id = sub, Username = name ?? sub });
    }

    private async Task<Resp<SsoUserProfile>> ExchangeGithubAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
    {
        var r = await ExchangeOAuth2Async("https://github.com/login/oauth/access_token", "https://api.github.com/user",
            s.ClientId, s.ClientSecret, code, redirectUri, "id", "login", "read:user", ct);
        return r;
    }

    private Task<Resp<SsoUserProfile>> ExchangeGoogleAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
        => ExchangeOAuth2Async("https://oauth2.googleapis.com/token", "https://www.googleapis.com/oauth2/v1/userinfo",
            s.ClientId, s.ClientSecret, code, redirectUri, "id", "name", "https://www.googleapis.com/auth/userinfo.profile", ct,
            extraForm: new Dictionary<string, string?> { ["grant_type"] = "authorization_code" });

    private Task<Resp<SsoUserProfile>> ExchangeMicrosoftAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
        => ExchangeOAuth2Async("https://login.microsoftonline.com/common/oauth2/v2.0/token", "https://graph.microsoft.com/v1.0/me",
            s.ClientId, s.ClientSecret, code, redirectUri, "id", "displayName", "user.read", ct,
            extraForm: new Dictionary<string, string?> { ["grant_type"] = "authorization_code" });

    private async Task<Resp<SsoUserProfile>> ExchangeDingtalkAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
    {
        // Dingtalk uses a JSON body for the token exchange and a different auth header for userinfo.
        var http = _http.CreateClient("sso");
        var body = JsonSerializer.Serialize(new
        {
            clientId = s.ClientId,
            clientSecret = s.ClientSecret,
            code,
            grantType = "authorization_code",
        });
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var resp = await http.PostAsync("https://api.dingtalk.com/v1.0/oauth2/userAccessToken", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return Resp<SsoUserProfile>.Fail($"dingtalk token http {resp.StatusCode}: {text}", RespCodes.InternalError);
        using var doc = JsonDocument.Parse(text);
        var accessToken = doc.RootElement.TryGetProperty("accessToken", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null;
        if (string.IsNullOrEmpty(accessToken)) return Resp<SsoUserProfile>.Fail("dingtalk: no accessToken", RespCodes.BadRequest);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.dingtalk.com/v1.0/contact/users/me");
        req.Headers.Add("x-acs-dingtalk-access-token", accessToken);
        using var uResp = await http.SendAsync(req, ct);
        var uText = await uResp.Content.ReadAsStringAsync(ct);
        if (!uResp.IsSuccessStatusCode) return Resp<SsoUserProfile>.Fail($"dingtalk user http {uResp.StatusCode}: {uText}", RespCodes.InternalError);
        using var uDoc = JsonDocument.Parse(uText);
        var id = uDoc.RootElement.TryGetProperty("unionId", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        var name = uDoc.RootElement.TryGetProperty("nick", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
        if (string.IsNullOrEmpty(id)) return Resp<SsoUserProfile>.Fail("dingtalk: no unionId", RespCodes.BadRequest);
        return Resp<SsoUserProfile>.Ok(new SsoUserProfile { Id = id, Username = name });
    }

    /// <summary>
    /// Generic OAuth2 authorization-code → access_token → userinfo flow shared by
    /// Casdoor, Github, Google and Microsoft (they only differ in URLs + field names).
    /// </summary>
    private async Task<Resp<SsoUserProfile>> ExchangeOAuth2Async(
        string tokenUrl, string userUrl,
        string clientId, string clientSecret,
        string code, string redirectUri,
        string idField, string usernameField, string scope,
        CancellationToken ct,
        Dictionary<string, string?>? extraForm = null)
    {
        var form = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
        };
        if (extraForm is not null)
            foreach (var kv in extraForm) form[kv.Key] = kv.Value;

        var token = await PostFormAsync(tokenUrl, form, ct);
        if (!token.IsSuccess()) return Resp<SsoUserProfile>.Fail(token.Message, token.Code);
        var accessToken = token.Data!.TryGetValue("access_token", out var at) ? at : null;
        if (string.IsNullOrEmpty(accessToken)) return Resp<SsoUserProfile>.Fail("no access_token in response", RespCodes.BadRequest);

        var http = _http.CreateClient("sso");
        using var req = new HttpRequestMessage(HttpMethod.Get, userUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return Resp<SsoUserProfile>.Fail($"userinfo http {resp.StatusCode}: {text}", RespCodes.InternalError);
        using var doc = JsonDocument.Parse(text);
        var id = doc.RootElement.TryGetProperty(idField, out var idEl) ? idEl.ValueKind switch
        {
            JsonValueKind.String => idEl.GetString(),
            JsonValueKind.Number => idEl.GetRawText(),
            _ => null,
        } : null;
        var name = doc.RootElement.TryGetProperty(usernameField, out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
        if (string.IsNullOrEmpty(id)) return Resp<SsoUserProfile>.Fail($"sso: no {idField} in userinfo", RespCodes.BadRequest);
        return Resp<SsoUserProfile>.Ok(new SsoUserProfile { Id = id, Username = name });
    }

    // --- helpers ---

    private async Task<Resp<Dictionary<string, string>>> PostFormAsync(string url, IReadOnlyDictionary<string, string?> form, CancellationToken ct)
    {
        var http = _http.CreateClient("sso");
        using var content = new FormUrlEncodedContent(form.Where(kv => kv.Value is not null).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!)));
        using var resp = await http.PostAsync(url, content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return Resp<Dictionary<string, string>>.Fail($"token http {resp.StatusCode}: {text}", RespCodes.InternalError);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) dict[p.Name] = p.Value.GetString()!;
            return Resp<Dictionary<string, string>>.Ok(dict);
        }
        catch
        {
            return Resp<Dictionary<string, string>>.Fail("token response is not json", RespCodes.InternalError);
        }
    }

    private async Task<string?> DiscoverAuthorizeEndpointAsync(string endpoint, CancellationToken ct)
    {
        var cfg = await DiscoverOidcConfigAsync(endpoint, ct);
        return cfg?.AuthorizationEndpoint;
    }

    private async Task<OidcConfig?> DiscoverOidcConfigAsync(string endpoint, CancellationToken ct)
    {
        var http = _http.CreateClient("sso");
        var url = endpoint.TrimEnd('/') + "/.well-known/openid-configuration";
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return new OidcConfig
            {
                AuthorizationEndpoint = doc.RootElement.GetProperty("authorization_endpoint").GetString()!,
                TokenEndpoint = doc.RootElement.GetProperty("token_endpoint").GetString()!,
            };
        }
        catch { return null; }
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in pairs)
        {
            if (kv.Value is null) continue;
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
        }
        return sb.ToString();
    }

    private static string Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    private async Task<SsoSettings> GetSettingsAsync(CancellationToken ct)
    {
        // Settings are read fresh each call — they're cheap and admins may change them.
        var rows = await _db.Settings.AsNoTracking().ToListAsync(ct);
        string Get(string key) => rows.FirstOrDefault(r => r.Key == key)?.Value ?? "";
        bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

        return new SsoSettings
        {
            Enabled = GetBool("sso_login_enabled"),
            Platform = Get("sso_login_platform"),
            CompatibilityMode = GetBool("sso_compatibility_mode"),
            AutoRegister = GetBool("sso_auto_register"),
            DefaultDir = Get("sso_default_dir") is { Length: > 0 } d ? d : "/",
            DefaultPermission = int.TryParse(Get("sso_default_permission"), out var p) ? p : 0,
            ClientId = Get("sso_client_id"),
            ClientSecret = Get("sso_client_secret"),
            Endpoint = Get("sso_endpoint_name"),
            ExtraScopes = Get("sso_extra_scopes"),
            OidcUsernameKey = Get("sso_oidc_username_key"),
        };
    }

    private sealed record OidcConfig
    {
        public required string AuthorizationEndpoint { get; init; }
        public required string TokenEndpoint { get; init; }
    }

    private sealed class SsoSettings
    {
        public bool Enabled { get; init; }
        public string Platform { get; init; } = "Casdoor";
        public bool CompatibilityMode { get; init; }
        public bool AutoRegister { get; init; }
        public string DefaultDir { get; init; } = "/";
        public int DefaultPermission { get; init; }
        public string ClientId { get; init; } = "";
        public string ClientSecret { get; init; } = "";
        public string Endpoint { get; init; } = "";
        public string ExtraScopes { get; init; } = "";
        public string OidcUsernameKey { get; init; } = "";
    }

    private sealed class SsoUserProfile
    {
        public string Id { get; set; } = "";
        public string? Username { get; set; }
    }
}
