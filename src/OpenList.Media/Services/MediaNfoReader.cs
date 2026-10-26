using System.Xml.Linq;

namespace OpenList.Media.Services;

/// <summary>NFO 解析出的一份元数据（与 provider 无关的中间形态）。</summary>
/// <param name="Title">标题。</param>
/// <param name="OriginalTitle">原始标题。</param>
/// <param name="Year">年份（year 缺失时回退 premiered/aired 的前 4 位）。</param>
/// <param name="Overview">简介（plot）。</param>
/// <param name="Rating">评分（0-10）。</param>
/// <param name="Genres">题材标签。</param>
/// <param name="TmdbId">uniqueid type=tmdb（回退裸 id 节点）。</param>
/// <param name="Persons">演职员（导演/主创/演员）。</param>
/// <param name="Studio">片商（studio），可为空。</param>
/// <param name="Series">系列（series，回退 set/collection），可为空。</param>
public sealed record MediaNfo(
    string? Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    double? Rating,
    IReadOnlyList<string> Genres,
    string? TmdbId,
    IReadOnlyList<MediaNfoPerson> Persons,
    string? Studio = null,
    string? Series = null);

/// <summary>NFO 解析出的一位演职员。</summary>
/// <param name="Name">姓名。</param>
/// <param name="Role">角色类型：director / creator / actor。</param>
/// <param name="Character">演员饰演的角色名。</param>
/// <param name="Order">演员表排序。</param>
public sealed record MediaNfoPerson(string Name, string Role, string? Character, int Order);

/// <summary>
/// Kodi 兼容 NFO 的解析器（纯函数，无 IO）。
/// <para>
/// 与写回端（<see cref="MediaMetadataService"/> 的 NFO 生成）互为镜像，但不要求严格
/// 对称：真实世界的 .nfo 来自 Jellyfin / Kodi / TinyMediaManager 等各家工具，字段
/// 拼写与文档结构都有出入，所以解析按「宽松取第一个能用的根元素」设计：
/// <list type="bullet">
/// <item>根元素接受 <c>movie</c> / <c>tvshow</c> / <c>episodedetails</c>；</item>
/// <item>容忍 XML 声明前后出现杂散内容（有些工具会先写一行注释）——多根时包一层再解析；</item>
/// <item>评分类节点可能带 <c>max</c> 属性（Kodi 9+），按 max 换算到 10 分制；</item>
/// <item>年份优先 <c>year</c>，缺失时取 <c>premiered</c>/<c>aired</c> 日期的前 4 位。</item>
/// </list>
/// </para>
/// 解析失败（非 XML、找不到可识别的根）返回 null，调用方按「无 NFO」继续走文件名解析。
/// </summary>
internal static class MediaNfoReader
{
    /// <summary>可识别的根元素名集合。</summary>
    private static readonly HashSet<string> RootNames = new(StringComparer.OrdinalIgnoreCase)
    { "movie", "tvshow", "episodedetails" };

    /// <summary>解析 NFO 内容；无法识别时返回 null。</summary>
    public static MediaNfo? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XElement? root;
        try
        {
            var doc = XDocument.Parse(xml);
            root = doc.Root is not null && RootNames.Contains(doc.Root.Name.LocalName)
                ? doc.Root
                : doc.Descendants().FirstOrDefault(e => RootNames.Contains(e.Name.LocalName));
        }
        catch (System.Xml.XmlException)
        {
            // 常见变体：声明/注释之后出现多个并列根元素。包一层伪根再试一次。
            try
            {
                var wrapped = XElement.Parse($"<nforoot>{xml}</nforoot>");
                root = wrapped.Descendants().FirstOrDefault(e => RootNames.Contains(e.Name.LocalName));
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }
        if (root is null) return null;

        var title = Text(root, "title");
        var original = Text(root, "originaltitle");
        var overview = Text(root, "plot");
        var year = YearOrPremiered(root);
        var rating = Rating(root);
        var genres = root.Elements("genre")
            .SelectMany(g => SplitList(g.Value))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tmdb = TmdbId(root);
        var persons = Persons(root);
        var studio = Text(root, "studio");
        var series = Series(root);

        if (title is null && original is null && year is null && overview is null
            && genres.Count == 0 && persons.Count == 0
            && studio is null && series is null)
            return null; // 结构认识但内容为空，视为没有元数据。

        return new MediaNfo(title, original, year, overview, rating, genres, tmdb, persons, studio, series);
    }

    /// <summary>取第一个非空子元素文本。</summary>
    private static string? Text(XElement root, string name)
        => root.Elements(name).Select(e => e.Value.Trim()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>年份：year 优先，缺失回退 premiered/aired 的前 4 位。</summary>
    private static int? YearOrPremiered(XElement root)
    {
        var direct = Text(root, "year");
        if (direct is not null && int.TryParse(direct, out var y)) return y;

        var date = Text(root, "premiered") ?? Text(root, "aired");
        if (date is not null && date.Length >= 4 && int.TryParse(date[..4], out var py))
            return py;
        return null;
    }

    /// <summary>评分：带 max 属性时按 max 换算到 10 分制，无属性视为已是 10 分制。</summary>
    private static double? Rating(XElement root)
    {
        var el = root.Elements("rating").FirstOrDefault(e => double.TryParse(e.Value, out _));
        if (el is null) return null;
        var value = double.Parse(el.Value);
        if (value < 0) return null;
        var maxAttr = el.Attribute("max") ?? el.Attribute("Max");
        if (maxAttr is not null && double.TryParse(maxAttr.Value, out var max) && max > 0 && max != 10)
            value = value * 10 / max;
        return Math.Clamp(Math.Round(value, 1), 0, 10);
    }

    /// <summary>uniqueid[type=tmdb] 优先，回退裸 id 节点。</summary>
    private static string? TmdbId(XElement root)
    {
        var unique = root.Elements("uniqueid")
            .FirstOrDefault(e => string.Equals(e.Attribute("type")?.Value, "tmdb", StringComparison.OrdinalIgnoreCase));
        if (unique is not null && !string.IsNullOrWhiteSpace(unique.Value)) return unique.Value.Trim();

        var id = Text(root, "id");
        return id is not null && id.All(char.IsDigit) ? id : null;
    }

    /// <summary>
    /// 系列：&lt;series&gt; 优先；回退 &lt;set&gt; —— Kodi 9+ 把合集写成
    /// <c>&lt;set&gt;&lt;name&gt;X&lt;/name&gt;...&lt;/set&gt;</c>，旧版是纯文本 <c>&lt;set&gt;X&lt;/set&gt;</c>，两种都认。
    /// </summary>
    private static string? Series(XElement root)
    {
        var direct = Text(root, "series");
        if (direct is not null) return direct;

        var set = root.Elements("set").FirstOrDefault();
        if (set is null) return null;
        var name = set.Elements("name").Select(e => e.Value.Trim()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (name is not null) return name;
        var text = set.Value.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>演职员：director / credits(creator) / actor 三类。</summary>
    private static List<MediaNfoPerson> Persons(XElement root)
    {
        var persons = new List<MediaNfoPerson>();
        foreach (var d in root.Elements("director"))
            foreach (var name in SplitList(d.Value))
                if (!string.IsNullOrWhiteSpace(name)) persons.Add(new MediaNfoPerson(name.Trim(), "director", null, 0));

        // Kodi 把剧集主创写在 credits 节点（与写回端的 creator 对应）。
        foreach (var c in root.Elements("credits").Concat(root.Elements("creator")))
            foreach (var name in SplitList(c.Value))
                if (!string.IsNullOrWhiteSpace(name)) persons.Add(new MediaNfoPerson(name.Trim(), "creator", null, 0));

        var order = 0;
        foreach (var a in root.Elements("actor"))
        {
            var name = Text(a, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var role = Text(a, "role");
            var ord = a.Elements("order").FirstOrDefault()?.Value;
            var parsed = int.TryParse(ord, out var o) && o >= 0 ? o : order;
            persons.Add(new MediaNfoPerson(name, "actor", role, parsed));
            order++;
        }
        return persons;
    }

    /// <summary>部分工具把多个值挤进一个节点（逗号或竖线分隔），拆开处理。</summary>
    private static IEnumerable<string> SplitList(string value)
        => value.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
