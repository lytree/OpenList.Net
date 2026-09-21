using System.Text.Json.Serialization;

namespace OpenList.Drivers.Cloud;

/// <summary>
/// 一对 OAuth 令牌。字段名与各家云盘 token 接口的 JSON 一致，
/// 便于驱动直接用 <see cref="System.Text.Json.JsonSerializer"/> 反序列化。
/// </summary>
public sealed class OAuthTokens
{
    /// <summary>访问令牌。</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>刷新令牌。部分服务刷新后不返回新值，此时应保留旧值。</summary>
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>构造令牌对。</summary>
    public OAuthTokens() { }

    /// <summary>以给定值构造令牌对。</summary>
    public OAuthTokens(string accessToken, string refreshToken)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
    }

    /// <summary>两个令牌是否都非空。</summary>
    [JsonIgnore]
    public bool IsComplete => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);
}
