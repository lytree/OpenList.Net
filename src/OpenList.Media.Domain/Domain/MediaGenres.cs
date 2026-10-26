using System.Text.Json;

namespace OpenList.Media.Domain;

/// <summary>
/// 题材列的读写口径：数据库里只有一列 JSON 文本（<c>GenresJson</c>），
/// 但调用方一律按集合处理。把解析/序列化/匹配收敛到这里，
/// 避免每个服务各写一份（历史上就出现过 <c>MediaGenreJson</c> 与
/// <c>UserMediaStateService.ParseGenres</c> 两套解析逻辑）。
/// </summary>
public static class MediaGenres
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>解析题材 JSON；空值或格式非法时返回空集合（不抛异常，脏数据不应打断列表查询）。</summary>
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json, Options);
            if (list is null) return [];
            return list.Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>把题材集合写成 JSON；<paramref name="genres"/> 为空时返回 null（列可为空）。</summary>
    public static string? Serialize(IEnumerable<string>? genres)
    {
        if (genres is null) return null;
        var list = genres.Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list, Options);
    }

    /// <summary>判断题材 JSON 中是否命中指定题材（忽略大小写与首尾空白）。</summary>
    public static bool Matches(string? json, string genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return false;
        var target = genre.Trim();
        foreach (var g in Parse(json))
        {
            if (string.Equals(g, target, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
