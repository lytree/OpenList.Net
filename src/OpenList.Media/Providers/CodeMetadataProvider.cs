using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;
using OpenList.Media.Services;

namespace OpenList.Media.Providers;

/// <summary>
/// 番号元数据 provider（<see cref="IMetadataProvider"/> 的第二个实现，Name=<c>code</c>）。
/// <para>
/// 与 TMDB 不同，番号没有公益的统一数据源，因此本 provider 不内置任何站点，
/// 而是把「去哪儿查」交给用户配置：设置项 <c>media_code_api_url</c> 填一个含
/// <c>{code}</code> 占位符的 JSON API 模板（自建聚合服务或任意兼容端点），
/// 未配置时 provider 不可用，条目照常走 TMDB。
/// </para>
/// <para>
/// 响应体按宽松映射解析（字段缺失即留空）：
/// <c>title / original_title / year(数字或日期串) / overview / rating / studio / series /
/// genres[](字符串数组) / poster / backdrop(URL) / actors[](对象或字符串数组)</c>。
/// </para>
/// </summary>
public sealed class CodeMetadataProvider : IMetadataProvider
{
    /// <summary>番号元数据 API 的 URL 模板设置项键。</summary>
    public const string ApiUrlSetting = "media_code_api_url";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingService _settings;

    /// <summary>创建番号 provider 实例。</summary>
    public CodeMetadataProvider(IHttpClientFactory httpFactory, ISettingService settings)
    {
        _httpFactory = httpFactory;
        _settings = settings;
    }

    /// <inheritdoc />
    public string Name => "code";

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(await GetApiUrlAsync(ct));

    /// <inheritdoc />
    public async Task<List<MetadataSearchResult>> SearchAsync(string title, int? year, MediaLibraryType type, string language, CancellationToken ct = default)
    {
        // 番号域没有"搜索候选"概念：能从文本里解析出番号就算唯一候选。
        var code = MediaNaming.ExtractCode(title ?? string.Empty);
        if (code is null) return [];
        return [new MetadataSearchResult { Id = code, Title = code }];
    }

    /// <inheritdoc />
    public async Task<MediaMetadata?> GetAsync(string providerId, MediaLibraryType type, string language, CancellationToken ct = default)
    {
        var template = await GetApiUrlAsync(ct);
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(providerId)) return null;

        var code = providerId.Trim().ToUpperInvariant();
        var url = template.Replace("{code}", Uri.EscapeDataString(code), StringComparison.OrdinalIgnoreCase);
        var doc = await GetJsonAsync(url, ct);
        if (doc is null) return null;
        var root = doc.RootElement;

        try
        {
            var meta = new MediaMetadata
            {
                Id = code,
                Title = GetString(root, "title"),
                OriginalTitle = GetString(root, "original_title"),
                Overview = GetString(root, "overview", "plot", "description"),
                Rating = GetDouble(root, "rating", "score"),
                Studio = GetString(root, "studio", "label", "maker"),
                Series = GetString(root, "series", "set"),
                PosterPath = GetString(root, "poster", "cover", "poster_url"),
                BackdropPath = GetString(root, "backdrop", "fanart"),
            };
            meta.Year = GetYear(root);

            if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                foreach (var g in genres.EnumerateArray())
                    if (g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString()))
                        meta.Genres.Add(g.GetString()!.Trim());

            if (root.TryGetProperty("actors", out var actors) && actors.ValueKind == JsonValueKind.Array)
            {
                var order = 0;
                foreach (var a in actors.EnumerateArray())
                {
                    string? name = a.ValueKind == JsonValueKind.String
                        ? a.GetString()
                        : GetString(a, "name", "actor");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    meta.Persons.Add(new MetadataPerson
                    {
                        Name = name.Trim(),
                        Role = "actor",
                        Character = a.ValueKind == JsonValueKind.Object ? NullIfEmpty(GetString(a, "character", "role")) : null,
                        Order = order++,
                    });
                }
            }
            return meta;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetImageAsync(string imagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var url = imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? imagePath : null;
        if (url is null) return null;
        try
        {
            var client = _httpFactory.CreateClient("code");
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> GetApiUrlAsync(CancellationToken ct)
    {
        var resp = await _settings.GetAsync(ApiUrlSetting, ct);
        var value = resp.Code == 200 ? resp.Data?.Value?.Trim() : null;
        return string.IsNullOrWhiteSpace(value) || !value.Contains("{code}", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("code");
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>年份：year 数字优先；字符串先按纯数字解析，失败再当日期串取前 4 位；
    /// 仍无则回退 release_date / premiered / date 的前 4 位。</summary>
    private static int? GetYear(JsonElement e)
    {
        if (e.TryGetProperty("year", out var y))
        {
            if (y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var n)) return n;
            var text = y.ValueKind == JsonValueKind.String ? y.GetString()?.Trim() : null;
            if (text is not null)
            {
                if (int.TryParse(text, out var parsed)) return parsed;
                if (text.Length >= 4 && int.TryParse(text[..4], out var py)) return py;
            }
        }
        var date = GetString(e, "release_date", "premiered", "date");
        return date.Length >= 4 && int.TryParse(date[..4], out var dy) ? dy : null;
    }

    private static string GetString(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        return string.Empty;
    }

    private static double? GetDouble(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d >= 0) return d;
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var p) && p >= 0) return p;
            }
        return null;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
