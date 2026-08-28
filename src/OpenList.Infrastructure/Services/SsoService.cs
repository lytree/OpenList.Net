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
/// SSO 编排服务，使用 OAuth2 授权码流程。所有配置都从设置存储读取
/// （第 7 组 —— 见 <c>DbInitializer</c>），因此管理员无需重启即可从前端调整。
/// 对应 Go 参考实现 <c>server/handles/ssologin.go</c>。
/// <para>
/// 支持的平台：
/// <list type="bullet">
/// <item><c>Casdoor</c> — 自托管的身份提供者（位于 <c>SSOEndpointName</c>）。</item>
/// <item><c>OIDC</c> — 通用 OpenID Connect（通过 <c>.well-known/openid-configuration</c> 发现）。</item>
/// <item><c>Github</c>、<c>Google</c>、<c>Microsoft</c>、<c>Dingtalk</c> — 已实现对接。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SsoService : ISsoService
{
    private readonly OpenListDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IUserService _users;
    private readonly IAuthService _auth;

    /// <summary>
    /// 创建 SSO 服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="http">HTTP 客户端工厂，用于访问身份提供者。</param>
    /// <param name="users">用户服务，用于查找或自动注册 SSO 用户。</param>
    /// <param name="auth">认证服务，用于签发 JWT。</param>
    public SsoService(OpenListDbContext db, IHttpClientFactory http, IUserService users, IAuthService auth)
    {
        _db = db;
        _http = http;
        _users = users;
        _auth = auth;
    }

    /// <summary>
    /// 判断 SSO 是否启用（需开启开关且配置了 client_id）。
    /// </summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        return s.Enabled && !string.IsNullOrWhiteSpace(s.ClientId);
    }

    /// <summary>
    /// 构建 SSO 授权 URL，供前端跳转到身份提供者完成登录。
    /// </summary>
    /// <param name="method">登录方式，应为 <c>get_sso_id</c> 或 <c>sso_get_token</c>。</param>
    /// <param name="redirectUri">回调地址（由调用方完整构造）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>授权跳转 URL；未启用或平台不支持时返回错误。</returns>
    public async Task<Resp<string>> GetAuthorizeUrlAsync(string method, string redirectUri, CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        if (!s.Enabled) return Resp<string>.Fail("sso login is not enabled", RespCodes.Forbidden);
        if (string.IsNullOrWhiteSpace(s.ClientId)) return Resp<string>.Fail("sso client_id not configured", RespCodes.BadRequest);
        if (method is not ("get_sso_id" or "sso_get_token"))
            return Resp<string>.Fail("invalid method", RespCodes.BadRequest);

        // 非兼容模式下回调始终为 /api/auth/sso_callback?method=...
        // 调用方会传入完整构造的 redirectUri，故这里无需知道主机名。
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

    /// <summary>
    /// 处理 SSO 回调：按平台换取令牌并获取用户资料，随后返回 SSO 编号或（可选）自动注册并签发 JWT。
    /// </summary>
    /// <param name="method">登录方式，<c>get_sso_id</c> 仅返回编号，<c>sso_get_token</c> 返回 JWT。</param>
    /// <param name="code">身份提供者返回的授权码。</param>
    /// <param name="state">回调附带的 state（此处未深入校验，仅透传）。</param>
    /// <param name="redirectUri">与授权时一致的回调地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>包含 SSO 编号或 JWT 的 <c>SsoCallbackResult</c>。</returns>
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

        // sso_get_token：查找本地用户，允许时自动注册，然后签发 JWT。
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
                // 用户名冲突 —— 追加 SSO id 后缀以消歧（与 Go 行为一致）。
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
            // 给新创建的账号打上 SSO 编号。
            await _users.UpdateAsync(u.Id, new UpdateUserReq { Id = u.Id, SsoID = userId }, ct);
        }

        if (u.Disabled) return Resp<SsoCallbackResult>.Fail("user disabled", RespCodes.Forbidden);
        var token = _auth.IssueToken(u);
        return Resp<SsoCallbackResult>.Ok(new SsoCallbackResult { Token = token });
    }

    // --- 各平台的令牌换取与用户信息获取 ---

    /// <summary>Casdoor 令牌换取 + 用户信息获取。</summary>
    private async Task<Resp<SsoUserProfile>> ExchangeCasdoorAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
    {
        var tokenUrl = s.Endpoint.TrimEnd('/') + "/api/login/oauth/access_token";
        var userUrl = s.Endpoint.TrimEnd('/') + "/api/userinfo";
        return await ExchangeOAuth2Async(tokenUrl, userUrl, s.ClientId, s.ClientSecret, code, redirectUri, "sub", "preferred_username", "profile", ct);
    }

    /// <summary>
    /// OIDC 令牌换取：发现端点、换取 id_token，并从 JWT 载荷中提取配置的用户名字段。
    /// </summary>
    private async Task<Resp<SsoUserProfile>> ExchangeOidcAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
    {
        // 对 OIDC 我们通过发现机制获得端点、换取 id_token，并从 JWT 载荷中裁取配置的
        // 用户名字段（不做签名校验 —— 身份提供者已通过 TLS 信任，对应 Go 宽松的 parseJWT）。
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

        // 解码 JWT 载荷（不做校验 —— 见上方注释）。
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

    /// <summary>GitHub 令牌换取 + 用户信息获取。</summary>
    private async Task<Resp<SsoUserProfile>> ExchangeGithubAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
    {
        var r = await ExchangeOAuth2Async("https://github.com/login/oauth/access_token", "https://api.github.com/user",
            s.ClientId, s.ClientSecret, code, redirectUri, "id", "login", "read:user", ct);
        return r;
    }

    /// <summary>Google 令牌换取 + 用户信息获取。</summary>
    private Task<Resp<SsoUserProfile>> ExchangeGoogleAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
        => ExchangeOAuth2Async("https://oauth2.googleapis.com/token", "https://www.googleapis.com/oauth2/v1/userinfo",
            s.ClientId, s.ClientSecret, code, redirectUri, "id", "name", "https://www.googleapis.com/auth/userinfo.profile", ct,
            extraForm: new Dictionary<string, string?> { ["grant_type"] = "authorization_code" });

    /// <summary>Microsoft 令牌换取 + 用户信息获取。</summary>
    private Task<Resp<SsoUserProfile>> ExchangeMicrosoftAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
        => ExchangeOAuth2Async("https://login.microsoftonline.com/common/oauth2/v2.0/token", "https://graph.microsoft.com/v1.0/me",
            s.ClientId, s.ClientSecret, code, redirectUri, "id", "displayName", "user.read", ct,
            extraForm: new Dictionary<string, string?> { ["grant_type"] = "authorization_code" });

    /// <summary>
    /// 钉钉令牌换取 + 用户信息获取（钉钉使用 JSON 请求体换令牌，userinfo 用专用头传令牌）。
    /// </summary>
    private async Task<Resp<SsoUserProfile>> ExchangeDingtalkAsync(SsoSettings s, string code, string redirectUri, CancellationToken ct)
    {
        // 钉钉换令牌用 JSON 请求体，获取 userinfo 时需要不同的认证头。
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
    /// 通用 OAuth2 授权码 → access_token → userinfo 流程，Casdoor/GitHub/Google/Microsoft 共用
    /// （它们仅在 URL 与字段名上不同）。
    /// </summary>
    /// <param name="tokenUrl">换取 access_token 的令牌端点。</param>
    /// <param name="userUrl">获取用户信息的端点。</param>
    /// <param name="clientId">客户端 ID。</param>
    /// <param name="clientSecret">客户端密钥。</param>
    /// <param name="code">授权码。</param>
    /// <param name="redirectUri">回调地址。</param>
    /// <param name="idField">用户信息中表示唯一标识的字段名。</param>
    /// <param name="usernameField">用户信息中表示用户名的字段名。</param>
    /// <param name="scope">请求的 scope。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="extraForm">需要追加到令牌请求表单的额外字段。</param>
    /// <returns>解析出的用户资料。</returns>
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

    // --- 辅助方法 ---

    /// <summary>以表单方式 POST 到令牌端点，并把 JSON 响应解析为字符串字典。</summary>
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

    /// <summary>通过 OIDC 发现机制解析授权端点。</summary>
    private async Task<string?> DiscoverAuthorizeEndpointAsync(string endpoint, CancellationToken ct)
    {
        var cfg = await DiscoverOidcConfigAsync(endpoint, ct);
        return cfg?.AuthorizationEndpoint;
    }

    /// <summary>拉取 OIDC 发现文档，解析授权端点与令牌端点。</summary>
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

    /// <summary>把键值对拼装为 application/x-www-form-urlencoded 查询字符串。</summary>
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

    /// <summary>base64url 解码并还原为 UTF-8 字符串。</summary>
    private static string Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    /// <summary>每次请求从设置存储读取并组装 SSO 配置（廉价，且管理员可能随时修改）。</summary>
    private async Task<SsoSettings> GetSettingsAsync(CancellationToken ct)
    {
        // 每次调用都重新读取设置 —— 开销小且管理员可能修改。
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

    /// <summary>OIDC 发现的配置结果。</summary>
    private sealed record OidcConfig
    {
        public required string AuthorizationEndpoint { get; init; }
        public required string TokenEndpoint { get; init; }
    }

    /// <summary>内部使用的 SSO 配置视图。</summary>
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

    /// <summary>内部使用的 SSO 用户资料视图。</summary>
    private sealed class SsoUserProfile
    {
        public string Id { get; set; } = "";
        public string? Username { get; set; }
    }
}