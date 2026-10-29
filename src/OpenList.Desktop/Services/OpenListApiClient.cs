using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenList.Desktop.Localization;

namespace OpenList.Desktop.Services;

// ---------------------------------------------------------------------
// 与 OpenList 服务端 API 对齐的 DTO。
// 服务端 JSON 为 camelCase；所有响应包在 { code, message, data } 里。
// ---------------------------------------------------------------------

public sealed class RespDto<T>
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
    public T? Data { get; set; }
    [JsonIgnore] public bool Ok => Code == 200;
}

/// <summary>API 调用失败（业务 code != 200 或 HTTP 失败）。</summary>
public sealed class ApiException(string message) : Exception(message);

/// <summary><c>GET /api/public/init_status</c> 的 data 载荷。</summary>
public sealed class InitStatusDto
{
    public bool Initialized { get; set; }
}

/// <summary>
/// OpenList HTTP API 客户端：登录/登出。
/// 鉴权用裸 token（服务端约定不带 Bearer 前缀）。
/// </summary>
public sealed class OpenListApiClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _token;

    public event Action? AuthChanged;

    public OpenListApiClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public string ServerUrl { get; set; } = "http://127.0.0.1:5244";
    public bool IsLoggedIn => _token is not null;

    /// <summary>当前持有的 token（只读）。调用方需临时换token 时应保存并在finally 还原。</summary>
    public string? Token => _token;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static async Task<T> UnwrapAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadFromJsonAsync<RespDto<T>>(JsonOpts, ct);
        if (body is null) throw new ApiException(Localizer.Get("api.parseFailed"));
        if (!body.Ok) throw new ApiException(body.Message);
        return body.Data!;
    }

    public async Task LoginAsync(string username, string password, string? otp, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"{ServerUrl.TrimEnd('/')}/api/auth/login",
            new { username, password, otpCode = otp }, JsonOpts, ct);
        var data = await UnwrapAsync<JsonElement>(resp, ct);
        _token = data.GetProperty("token").GetString();
        AuthChanged?.Invoke();
    }

    public void SetToken(string? token)
    {
        _token = token;
        AuthChanged?.Invoke();
    }

    public async Task LogoutAsync()
    {
        if (_token is null) return;
        try { using var resp = await _http.PostAsync($"{ServerUrl.TrimEnd('/')}/api/auth/logout", null); }
        catch { /* 尽力而为 */ }
        _token = null;
        AuthChanged?.Invoke();
    }

    /// <summary>系统是否已有管理员账号（<c>GET /api/public/init_status</c>）。</summary>
    public async Task<bool> IsInitializedAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{ServerUrl.TrimEnd('/')}/api/public/init_status", ct);
        var data = await UnwrapAsync<InitStatusDto>(resp, ct);
        return data?.Initialized ?? false;
    }

    /// <summary>
    /// 创建系统首个管理员（<c>POST /api/public/init/setup</c>）。
    /// 仅在尚无管理员时可用；已初始化时服务端返回 400（这里表现为 <see cref="ApiException"/>）。
    /// </summary>
    public async Task CreateInitialAdminAsync(
        string username, string password, string? siteTitle = null, CancellationToken ct = default)
    {
        // 服务端 InitSetupReq 的站点名带 [JsonPropertyName("site_title")]，
        // camelCase 策略不会转换这个显式名字，所以这里必须自己写 snake_case。
        var body = new Dictionary<string, object?>
        {
            ["username"] = username,
            ["password"] = password,
        };
        if (!string.IsNullOrWhiteSpace(siteTitle)) body["site_title"] = siteTitle;

        using var resp = await _http.PostAsJsonAsync(
            $"{ServerUrl.TrimEnd('/')}/api/public/init/setup", body, JsonOpts, ct);
        await UnwrapAsync<JsonElement?>(resp, ct);
    }

    /// <summary>重置内置管理员密码（<c>POST /api/admin/user/reset_admin_password</c>）。
    /// 需要 admin 身份；服务端不要求旧密码 —— 内置admin 被刻意设为不可删除 / 不可改名，
    /// 若改密还要求旧密码，忘记密码就彻底失去管理入口。
    /// </summary>
    public async Task ResetAdminPasswordAsync(string password, CancellationToken ct = default)
    {
        using var resp = await SendAuthedAsync(HttpMethod.Post,
            $"{ServerUrl.TrimEnd('/')}/api/admin/user/reset_admin_password",
            new { password }, ct);
        await UnwrapAsync<object?>(resp, ct);
    }

    /// <summary>发送带鉴权头的请求（服务端约定裸 token，不加 Bearer 前缀）。</summary>
    private async Task<HttpResponseMessage> SendAuthedAsync(
        HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: JsonOpts);
        if (_token is not null)
            req.Headers.TryAddWithoutValidation("Authorization", _token);
        return await _http.SendAsync(req, ct);
    }

    public void Dispose() => _http.Dispose();
}
