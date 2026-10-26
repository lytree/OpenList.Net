using OpenList.Media.Domain;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Providers;

/// <summary>
/// TMDB 元数据 provider。使用 v3 API Key（32 位）或 v4 Bearer Token（以 eyJ 开头）。
/// API Key 读取设置项 <c>media_tmdb_api_key</c>；未配置时 <see cref="IsConfigured"/> 为 false，
/// 所有调用返回空结果而不是抛异常，便于媒体库在未配置时仍可正常浏览。
/// </summary>
public sealed class TmdbMetadataProvider : IMetadataProvider
{
    /// <summary>TMDB API 基础地址。</summary>
    private const string ApiBase = "https://api.themoviedb.org/3";
    /// <summary>TMDB 图片基础地址。</summary>
    private const string ImageBase = "https://image.tmdb.org/t/p/original";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingService _settings;

    /// <summary>创建 TMDB provider 实例。</summary>
    /// <param name="httpFactory">HTTP 客户端工厂。</param>
    /// <param name="settings">设置服务（读取 API Key）。</param>
    public TmdbMetadataProvider(IHttpClientFactory httpFactory, ISettingService settings)
    {
        _httpFactory = httpFactory;
        _settings = settings;
    }

    /// <inheritdoc />
    public string Name => "tmdb";

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(await GetApiKeyAsync(ct));

    /// <inheritdoc />
    public async Task<List<MetadataSearchResult>> SearchAsync(string title, int? year, MediaLibraryType type, string language, CancellationToken ct = default)
    {
        var key = await GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title)) return [];

        var isTv = type == MediaLibraryType.TvShow;
        var url = $"{ApiBase}/search/{(isTv ? "tv" : "movie")}?query={Uri.EscapeDataString(title)}&language={Uri.EscapeDataString(language)}";
        if (year is not null) url += isTv ? $"&first_air_date_year={year}" : $"&year={year}";

        using var doc = await GetJsonAsync(url, key, ct);
        if (doc is null) return [];

        var results = new List<MetadataSearchResult>();
        if (!doc.RootElement.TryGetProperty("results", out var arr)) return results;
        foreach (var e in arr.EnumerateArray())
        {
            var dateKey = isTv ? "first_air_date" : "release_date";
            results.Add(new MetadataSearchResult
            {
                Id = e.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : string.Empty,
                Title = GetString(e, isTv ? "name" : "title"),
                OriginalTitle = GetString(e, isTv ? "original_name" : "original_title"),
                Year = ParseYear(GetString(e, dateKey)),
                Overview = GetString(e, "overview"),
                PosterPath = GetString(e, "poster_path"),
                Rating = e.TryGetProperty("vote_average", out var va) ? va.GetDouble() : null,
            });
        }
        return results;
    }

    /// <inheritdoc />
    public async Task<MediaMetadata?> GetAsync(string providerId, MediaLibraryType type, string language, CancellationToken ct = default)
    {
        var key = await GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key)) return null;

        var isTv = type == MediaLibraryType.TvShow;
        var url = $"{ApiBase}/{(isTv ? "tv" : "movie")}/{providerId}?append_to_response=credits&language={Uri.EscapeDataString(language)}";
        using var doc = await GetJsonAsync(url, key, ct);
        if (doc is null) return null;

        var root = doc.RootElement;
        var meta = new MediaMetadata
        {
            Id = providerId,
            Title = GetString(root, isTv ? "name" : "title"),
            OriginalTitle = GetString(root, isTv ? "original_name" : "original_title"),
            Year = ParseYear(GetString(root, isTv ? "first_air_date" : "release_date")),
            Overview = GetString(root, "overview"),
            PosterPath = GetString(root, "poster_path"),
            BackdropPath = GetString(root, "backdrop_path"),
            Rating = root.TryGetProperty("vote_average", out var va) ? va.GetDouble() : null,
        };

        if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            foreach (var g in genres.EnumerateArray())
                meta.Genres.Add(GetString(g, "name"));

        if (root.TryGetProperty("credits", out var credits))
        {
            if (credits.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
            {
                var order = 0;
                foreach (var c in cast.EnumerateArray().Take(20))
                {
                    meta.Persons.Add(new MetadataPerson
                    {
                        Name = GetString(c, "name"),
                        ProviderId = c.TryGetProperty("id", out var pid) ? pid.GetInt64().ToString() : null,
                        Role = "actor",
                        Character = GetString(c, "character"),
                        ProfilePath = GetString(c, "profile_path"),
                        Order = order++,
                    });
                }
            }
            if (credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in crew.EnumerateArray())
                {
                    var job = GetString(c, "job");
                    var role = job switch
                    {
                        "Director" => "director",
                        "Writer" or "Screenplay" or "Story" => "writer",
                        _ => null,
                    };
                    if (role is null) continue;
                    if (meta.Persons.Any(p => p.ProviderId == (c.TryGetProperty("id", out var cid) ? cid.GetInt64().ToString() : null) && p.Role == role))
                        continue;
                    meta.Persons.Add(new MetadataPerson
                    {
                        Name = GetString(c, "name"),
                        ProviderId = c.TryGetProperty("id", out var cid2) ? cid2.GetInt64().ToString() : null,
                        Role = role,
                        ProfilePath = GetString(c, "profile_path"),
                        Order = 100,
                    });
                }
            }
            // 剧集创建者。
            if (isTv && root.TryGetProperty("created_by", out var creators) && creators.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in creators.EnumerateArray())
                {
                    meta.Persons.Add(new MetadataPerson
                    {
                        Name = GetString(c, "name"),
                        ProviderId = c.TryGetProperty("id", out var cid) ? cid.GetInt64().ToString() : null,
                        Role = "creator",
                        ProfilePath = GetString(c, "profile_path"),
                        Order = 50,
                    });
                }
            }
        }
        return meta;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetImageAsync(string imagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var url = imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? imagePath : ImageBase + imagePath;
        try
        {
            var client = _httpFactory.CreateClient("tmdb");
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>读取并校验 API Key；支持 v3 key 与 v4 Bearer token 两种形态。</summary>
    private async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        var resp = await _settings.GetAsync("media_tmdb_api_key", ct);
        var key = resp.Code == 200 ? resp.Data?.Value?.Trim() : null;
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>发起 GET 请求并解析 JSON；失败返回 null（刮削按"未匹配"处理）。</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, string apiKey, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("tmdb");
            var finalUrl = apiKey.StartsWith("eyJ", StringComparison.Ordinal)
                ? url
                : url + (url.Contains('?') ? "&" : "?") + "api_key=" + apiKey;

            using var req = new HttpRequestMessage(HttpMethod.Get, finalUrl);
            if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int? ParseYear(string date)
        => date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : null;
}
