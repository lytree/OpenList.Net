using System.Net.Http.Headers;
using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Providers;

/// <summary>
/// TMDB 创作者作品流 provider：读 <c>GET /3/person/{id}/combined_credits</c>，
/// 把 <c>cast</c> 与 <c>crew</c> 合并成「这个人名下的全部作品」。
/// <para>
/// 与 <see cref="TmdbMetadataProvider"/> 同款鉴权（v3 API Key 读 <c>media_tmdb_api_key</c>，
/// 或 v4 Bearer Token）与图片基地址。差别在失败语义：刮削失败按「未匹配」吞掉，
/// 这里则**抛异常** —— 关注检查要把原因写进 <c>LastError</c> 给用户看，吞掉就只剩「没更新」。
/// </para>
/// </summary>
public sealed class TmdbPersonFeedProvider : IPersonFeedProvider
{
    /// <summary>TMDB API 基础地址。</summary>
    public const string ApiBase = "https://api.themoviedb.org/3";
    /// <summary>TMDB 图片基础地址（与 <see cref="TmdbMetadataProvider"/> 一致）。</summary>
    public const string ImageBase = "https://image.tmdb.org/t/p/original";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingService _settings;

    /// <summary>创建 TMDB 作品流 provider 实例。</summary>
    /// <param name="httpFactory">HTTP 客户端工厂（命名客户端 <c>tmdb</c>）。</param>
    /// <param name="settings">设置服务（读取 API Key 与元数据语言）。</param>
    public TmdbPersonFeedProvider(IHttpClientFactory httpFactory, ISettingService settings)
    {
        _httpFactory = httpFactory;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersonFeedWork>> GetWorksAsync(string tmdbPersonId, CancellationToken ct = default)
    {
        var key = await GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("metadata provider is not configured (set media_tmdb_api_key)");
        if (string.IsNullOrWhiteSpace(tmdbPersonId))
            throw new InvalidOperationException("person has no tmdb id");

        var language = await ResolveLanguageAsync(ct);
        var url = $"{ApiBase}/person/{Uri.EscapeDataString(tmdbPersonId)}/combined_credits" +
                  $"?language={Uri.EscapeDataString(language)}";

        var client = _httpFactory.CreateClient("tmdb");
        var finalUrl = key.StartsWith("eyJ", StringComparison.Ordinal)
            ? url
            : url + "&api_key=" + Uri.EscapeDataString(key);
        using var req = new HttpRequestMessage(HttpMethod.Get, finalUrl);
        if (key.StartsWith("eyJ", StringComparison.Ordinal))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"tmdb combined_credits returned {(int)resp.StatusCode}");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        var works = new List<PersonFeedWork>();
        var seen = new HashSet<(string Type, string Id)>();

        // 先 cast 后 crew：同一部作品既演又导时只留一行，演员的 character 优先。
        if (root.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
            AddAll(cast, isCast: true, works, seen);
        if (root.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
            AddAll(crew, isCast: false, works, seen);
        return works;
    }

    /// <summary>把一段演职员数组追加进结果；只收 movie / tv，按 (类型, id) 去重。</summary>
    private static void AddAll(JsonElement arr, bool isCast, List<PersonFeedWork> works, HashSet<(string, string)> seen)
    {
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var mediaType = GetString(e, "media_type");
            if (mediaType is not ("movie" or "tv")) continue;
            var id = e.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64().ToString()
                : GetString(e, "id");
            if (id.Length == 0) continue;
            if (!seen.Add((mediaType, id))) continue;

            var isTv = mediaType == "tv";
            var title = GetString(e, isTv ? "name" : "title");
            if (title.Length == 0) title = GetString(e, isTv ? "original_name" : "original_title");

            string? role;
            if (isCast)
            {
                // 没写角色名的演出（纪录片出镜、群演）给个兜底，免得更新流里这一格是空的。
                role = GetString(e, "character");
                if (role.Length == 0) role = "actor";
            }
            else
            {
                role = GetString(e, "job");
                if (role.Length == 0) role = "crew";
            }

            works.Add(new PersonFeedWork
            {
                MediaType = mediaType,
                ExternalId = id,
                Title = title,
                ReleaseDate = GetString(e, isTv ? "first_air_date" : "release_date"),
                PosterPath = GetString(e, "poster_path"),
                Role = role,
            });
        }
    }

    /// <summary>读取并校验 API Key；支持 v3 key 与 v4 Bearer token 两种形态。</summary>
    private async Task<string?> GetApiKeyAsync(CancellationToken ct)
    {
        var resp = await _settings.GetAsync("media_tmdb_api_key", ct);
        var key = resp.Code == 200 ? resp.Data?.Value?.Trim() : null;
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>作品标题的语言：取全局元数据语言设置，缺省 zh-CN。</summary>
    private async Task<string> ResolveLanguageAsync(CancellationToken ct)
    {
        var resp = await _settings.GetAsync("media_metadata_language", ct);
        var value = resp.Code == 200 ? resp.Data?.Value : null;
        return string.IsNullOrWhiteSpace(value) ? "zh-CN" : value.Trim();
    }

    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
