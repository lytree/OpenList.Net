using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Drivers.Cloud.DiskApi;

namespace OpenList.Drivers.Cloud.PikPak;

/// <summary>
/// PikPak 云盘驱动。基于官方开放 API（<c>api-drive.mypikpak.com</c>）：
/// 用户名/密码登录或 refresh_token 续期，支持列举、直链下载、建目录、删除、移动/改名。
/// <para>
/// 不实现上传（<see cref="NoUpload"/> = true）：本驱动的定位是"云下载来源"——
/// 把 PikPak 上的内容通过既有复制/下载任务转存到目标存储，读取能力已足够。
/// </para>
/// </summary>
public sealed class PikPakDriver : CloudDiskDriverBase
{
    /// <summary>API 根地址。</summary>
    internal const string ApiUrl = "https://api-drive.mypikpak.com";
    /// <summary>PikPak Web 客户端 id（公开常量，与社区实现一致）。</summary>
    internal const string DefaultClientId = "YUMx5nI8ZU8Ap8pm";
    /// <summary>PikPak Web 客户端 secret（公开常量）。</summary>
    internal const string DefaultClientSecret = "dbw2OtmVEeuUvIptb1Coyg";

    /// <inheritdoc />
    public override string DriverName => "PikPak";

    /// <inheritdoc />
    public override bool NoUpload => true;

    /// <inheritdoc />
    public override string DefaultRoot => string.Empty;

    /// <inheritdoc />
    public override string Alert =>
        "info|基于 PikPak 开放 API，已支持浏览/直链/建目录/删除/移动；上传不支持（定位为云下载来源）。";

    /// <inheritdoc />
    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    /// <summary>配置字段清单。</summary>
    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new() { Name = "username", Help = "PikPak username (email or phone)", ValueType = typeof(string) },
        new() { Name = "password", Help = "PikPak password", ValueType = typeof(string) },
        new() { Name = "refresh_token", Help = "OAuth refresh token (filled after first sign-in)", ValueType = typeof(string) },
        new() { Name = "root_folder_id", Help = "Root folder id (empty means drive root)", ValueType = typeof(string) },
        new() { Name = "client_id", Help = "Keep it empty to use the built-in web client", ValueType = typeof(string) },
        new() { Name = "client_secret", Help = "Keep it empty to use the built-in web client", ValueType = typeof(string) },
        new() { Name = "limit_rate", Help = "Requests per second limit (0 = unlimited)", ValueType = typeof(float), Default = 0f },
    ];

    /// <summary>附加配置。</summary>
    public PikPakAddition Addition { get; private set; } = new();

    /// <inheritdoc />
    protected override object AdditionObject => Addition;

    /// <inheritdoc />
    protected override ICloudDiskApi CreateApi() => new PikPakApi(this);

    /// <summary>当前访问令牌。</summary>
    internal string AccessToken { get; private set; } = string.Empty;

    /// <summary>令牌过期时间（UTC）。</summary>
    internal DateTimeOffset TokenExpiresAt { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>挂载根目录 id（空字符串表示网盘根）。</summary>
    internal string RootFolderId => Addition.RootFolderId ?? string.Empty;

    /// <summary>当前配置的 client id。</summary>
    internal string ClientId => string.IsNullOrWhiteSpace(Addition.ClientId) ? DefaultClientId : Addition.ClientId!;

    /// <summary>当前配置的 client secret。</summary>
    internal string ClientSecret => string.IsNullOrWhiteSpace(Addition.ClientSecret) ? DefaultClientSecret : Addition.ClientSecret!;

    /// <inheritdoc />
    public override async Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<PikPakAddition>();
        if (Addition.LimitRate > 0) ConfigureRateLimit(Addition.LimitRate);

        if (string.IsNullOrWhiteSpace(Addition.RefreshToken) &&
            (string.IsNullOrWhiteSpace(Addition.Username) || string.IsNullOrWhiteSpace(Addition.Password)))
            throw new InvalidOperationException("username/password or refresh_token is required");

        await EnsureTokenAsync(force: false, ct).ConfigureAwait(false);
        AttachFileSystem();
    }

    /// <summary>确保访问令牌有效；过期或强制时用 refresh_token（优先）或账号密码重新获取。</summary>
    internal async Task EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (!force && !string.IsNullOrEmpty(AccessToken) && TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return;

        await WaitRateLimitAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/v1/auth/token");
        req.Content = Json(new
        {
            client_id = ClientId,
            client_secret = ClientSecret,
            grant_type = "refresh_token",
            refresh_token = Addition.RefreshToken,
        });

        if (string.IsNullOrWhiteSpace(Addition.RefreshToken))
        {
            req.RequestUri = new Uri(ApiUrl + "/v1/auth/signin");
            req.Content = Json(new
            {
                client_id = ClientId,
                client_secret = ClientSecret,
                username = Addition.Username,
                password = Addition.Password,
            });
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"PikPak auth failed: {Truncate(text)}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        AccessToken = Str(root, "access_token") ?? string.Empty;
        var refresh = Str(root, "refresh_token");
        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var n) ? n : 7200;
        TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));

        if (!string.IsNullOrEmpty(refresh)) Addition.RefreshToken = refresh;
        // 令牌会过期，回写数据库避免每次启动都重新登录。
        await SaveAdditionAsync(AdditionObject, ct).ConfigureAwait(false);
    }

    /// <summary>构造带鉴权头的请求。</summary>
    internal async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        await EnsureTokenAsync(force: false, ct).ConfigureAwait(false);
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        req.Headers.Add("X-Client-ID", ClientId);
        if (body is not null) req.Content = Json(body);
        return req;
    }

    internal static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    internal static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}

/// <summary>PikPak API 客户端：把云端 JSON 映射成 <see cref="CloudDiskNode"/>。</summary>
internal sealed class PikPakApi : ICloudDiskApi
{
    private readonly PikPakDriver _driver;

    internal PikPakApi(PikPakDriver driver) => _driver = driver;

    public string RootId => _driver.RootFolderId;

    public HttpClient HttpClient => CloudHttpClient.Shared;

    public async Task<IReadOnlyList<CloudDiskNode>> ListAsync(string parentId, CancellationToken ct)
    {
        var result = new List<CloudDiskNode>();
        var pageToken = string.Empty;

        for (var page = 0; page < 100; page++)
        {
            var url = $"{PikPakDriver.ApiUrl}/drive/v1/files?parent_id={Uri.EscapeDataString(parentId)}" +
                      $"&limit=100&thumbnail_size=SIZE_LARGE&with_audit=true" +
                      (string.IsNullOrEmpty(pageToken) ? string.Empty : $"&page_token={Uri.EscapeDataString(pageToken)}");

            using var req = await _driver.AuthorizedAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) break;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) break;

            foreach (var f in files.EnumerateArray())
                result.Add(ToNode(f, parentId));

            pageToken = PikPakDriver.Str(doc.RootElement, "next_page_token") ?? string.Empty;
            if (string.IsNullOrEmpty(pageToken)) break;
        }
        return result;
    }

    public async Task<CloudDiskNode?> GetAsync(string id, CancellationToken ct)
    {
        using var req = await _driver.AuthorizedAsync(HttpMethod.Get, $"{PikPakDriver.ApiUrl}/drive/v1/files/{id}", null, ct)
            .ConfigureAwait(false);
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return ToNode(doc.RootElement, PikPakDriver.Str(doc.RootElement, "parent_id") ?? string.Empty);
    }

    public async Task<string?> GetDownloadUrlAsync(string id, CancellationToken ct)
    {
        // 列表里的 web_content_link 有时为空，按 id 再取一次详情即可拿到新鲜直链。
        var node = await GetAsync(id, ct).ConfigureAwait(false);
        if (node is null) return null;

        using var req = await _driver.AuthorizedAsync(HttpMethod.Get, $"{PikPakDriver.ApiUrl}/drive/v1/files/{id}", null, ct)
            .ConfigureAwait(false);
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var direct = PikPakDriver.Str(doc.RootElement, "web_content_link");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        if (doc.RootElement.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                var url = PikPakDriver.Str(link, "url");
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        return null;
    }

    public async Task<CloudDiskNode> MakeDirAsync(string parentId, string name, CancellationToken ct)
    {
        using var req = await _driver.AuthorizedAsync(HttpMethod.Post, $"{PikPakDriver.ApiUrl}/drive/v1/files",
            new { kind = "drive#folder", parent_id = parentId, name }, ct).ConfigureAwait(false);
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new IOException($"PikPak mkdir failed: {text}");

        using var doc = JsonDocument.Parse(text);
        return ToNode(doc.RootElement, parentId);
    }

    public async Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        using var req = await _driver.AuthorizedAsync(HttpMethod.Post, $"{PikPakDriver.ApiUrl}/drive/v1/files:batchDelete",
            new { ids }, ct).ConfigureAwait(false);
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"PikPak delete failed: {await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
    }

    public async Task MoveAsync(string id, string? newParentId, string? newName, CancellationToken ct)
    {
        var body = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(newParentId)) body["parent_id"] = newParentId;
        if (!string.IsNullOrEmpty(newName)) body["name"] = newName;

        using var req = await _driver.AuthorizedAsync(new HttpMethod("PATCH"),
            $"{PikPakDriver.ApiUrl}/drive/v1/files/{id}", body, ct).ConfigureAwait(false);
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"PikPak move failed: {await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
    }

    /// <summary>把 PikPak 文件 JSON 映射为节点。</summary>
    private static CloudDiskNode ToNode(JsonElement e, string fallbackParent)
    {
        var kind = PikPakDriver.Str(e, "kind") ?? string.Empty;
        return new CloudDiskNode
        {
            Id = PikPakDriver.Str(e, "id") ?? string.Empty,
            ParentId = PikPakDriver.Str(e, "parent_id") ?? fallbackParent,
            Name = PikPakDriver.Str(e, "name") ?? string.Empty,
            IsFolder = kind.Contains("folder", StringComparison.OrdinalIgnoreCase),
            Size = PikPakDriver.Long(e, "size"),
            Modified = ParseTime(PikPakDriver.Str(e, "modified_time")),
            Created = ParseTime(PikPakDriver.Str(e, "created_time")),
        };
    }

    private static DateTimeOffset ParseTime(string? value)
        => DateTimeOffset.TryParse(value, out var t) ? t : DateTimeOffset.MinValue;
}

/// <summary>PikPak 附加配置。</summary>
public sealed class PikPakAddition
{
    /// <summary>用户名（邮箱或手机号）。</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>密码。</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>刷新令牌（首次登录后自动写入）。</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    /// <summary>挂载根目录 id，空表示网盘根。</summary>
    [JsonPropertyName("root_folder_id")]
    public string? RootFolderId { get; set; }

    /// <summary>自定义 client id，空表示使用内置 Web 客户端。</summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    /// <summary>自定义 client secret。</summary>
    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    /// <summary>每秒请求数上限，0 表示不限。</summary>
    [JsonPropertyName("limit_rate")]
    public float LimitRate { get; set; }
}
