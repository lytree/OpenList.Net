using System.Net.Http.Json;
using System.Text.Json;

namespace OpenList.Drivers.Cloud;

/// <summary>
/// OAuth 令牌刷新器：带重试地执行一次「用 refresh_token 换新 token」，
/// 语义对齐 Go 驱动的 <c>refreshToken()</c>（首次调用 + 最多 3 次重试）。
/// </summary>
/// <remarks>
/// 刷新本身不负责持久化，也不修改驱动状态 —— 驱动拿到结果后自行
/// <c>ApplyTokens</c> 并调用 <c>SaveAdditionAsync</c>，职责分明也便于单测。
/// </remarks>
public sealed class OAuthTokenRefresher
{
    private readonly Func<CancellationToken, Task<OAuthTokens>> _refreshOnce;

    /// <summary>
    /// 构造刷新器。
    /// </summary>
    /// <param name="refreshOnce">执行一次刷新的委托（发请求 + 解析响应）。</param>
    public OAuthTokenRefresher(Func<CancellationToken, Task<OAuthTokens>> refreshOnce)
        => _refreshOnce = refreshOnce ?? throw new ArgumentNullException(nameof(refreshOnce));

    /// <summary>
    /// 总尝试次数（含首次）。默认 4，对齐 Go 的「1 次 + 3 次重试」。
    /// </summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>
    /// 每次失败后的回调（异常, 第几次尝试）。用于打日志；不影响重试流程。
    /// </summary>
    public Action<Exception, int>? OnFailure { get; init; }

    /// <summary>
    /// 执行刷新，失败时按 <see cref="MaxAttempts"/> 重试。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>刷新得到的令牌对。</returns>
    /// <exception cref="InvalidOperationException">
    /// 所有尝试均失败，或令牌接口返回的 <c>access_token</c> 为空时抛出。
    /// </exception>
    /// <exception cref="OperationCanceledException">外部取消时原样抛出，不重试。</exception>
    public async Task<OAuthTokens> RefreshAsync(CancellationToken ct = default)
    {
        if (MaxAttempts < 1)
            throw new InvalidOperationException($"{nameof(MaxAttempts)} must be at least 1, got {MaxAttempts}.");

        Exception? last = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tokens = await _refreshOnce(ct).ConfigureAwait(false);

                // 某些服务在 refresh_token 失效时返回 200 + 空 token，这种「静默失败」
                // 必须当成失败处理，否则会把空 token 写回数据库、把配置彻底搞坏。
                if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
                    throw new InvalidOperationException("token endpoint returned an empty access_token");

                return tokens;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方主动取消：不重试，直接向上抛。
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                OnFailure?.Invoke(ex, attempt);
            }
        }

        // 把最后一次的失败原因拼进消息：日志里只看到「refresh failed after 4 attempt(s)」
        // 而没有具体原因，排查时会非常痛苦（内层异常在日志格式里常常被丢掉）。
        throw new InvalidOperationException(
            $"OAuth token refresh failed after {MaxAttempts} attempt(s): {last?.Message}", last);
    }

    /// <summary>
    /// 为「POST JSON、响应 JSON 里含 access_token/refresh_token」这一最常见的云盘
    /// 令牌接口形态构造刷新器（阿里云盘、115 Open、Dropbox 等均属此形态）。
    /// </summary>
    /// <param name="http">用于发请求的 HTTP 客户端。</param>
    /// <param name="url">令牌接口地址。</param>
    /// <param name="requestBody">构造请求体的委托，每次尝试时调用。</param>
    /// <param name="limiter">可选的限流器，发请求前等待。</param>
    /// <param name="maxAttempts">总尝试次数（含首次）。</param>
    /// <param name="onFailure">每次失败后的回调，可为空。</param>
    /// <returns>配置好的刷新器。</returns>
    public static OAuthTokenRefresher ForJsonEndpoint(
        HttpClient http,
        string url,
        Func<object> requestBody,
        RequestRateLimiter? limiter = null,
        int maxAttempts = 4,
        Action<Exception, int>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(requestBody);

        return new OAuthTokenRefresher(async ct =>
        {
            if (limiter is not null) await limiter.WaitAsync(ct).ConfigureAwait(false);

            using var resp = await http.PostAsJsonAsync(url, requestBody(), ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"token endpoint {(int)resp.StatusCode} ({resp.StatusCode}): {Truncate(body)}");

            try
            {
                return JsonSerializer.Deserialize<OAuthTokens>(body) ?? new OAuthTokens();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"token endpoint returned invalid JSON: {Truncate(body)}", ex);
            }
        })
        {
            MaxAttempts = maxAttempts,
            OnFailure = onFailure,
        };
    }

    /// <summary>截断过长的响应体，避免把整个页面塞进异常消息和日志。</summary>
    private static string Truncate(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";
}
