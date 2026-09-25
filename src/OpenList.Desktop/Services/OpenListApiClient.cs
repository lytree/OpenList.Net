using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static async Task<T> UnwrapAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadFromJsonAsync<RespDto<T>>(JsonOpts, ct);
        if (body is null) throw new ApiException("响应解析失败");
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

    public void Dispose() => _http.Dispose();
}
