using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 直链（<c>/d/</c> 302 跳转）的查询参数转发规则，对应站点设置
/// <c>forward_direct_link_params</c> 与 <c>ignore_direct_link_params</c>。
/// </summary>
/// <remarks>
/// <para>
/// <c>/d/</c> 拿到的直链来自驱动（云盘预签名 URL）。客户端请求 <c>/d/a.mp4?download=1</c> 时，
/// 这个 <c>download=1</c> 默认<b>不会</b>出现在跳转目标上 —— 对云端来说它只是噪声，
/// 但有些场景（上游自己的参数、第三方 CDN 统计）需要把它带过去，所以给了开关：
/// </para>
/// <list type="bullet">
/// <item><c>forward_direct_link_params</c>（默认 false）：是否把当前请求的查询参数拼到直链上；</item>
/// <item><c>ignore_direct_link_params</c>（默认空）：逗号/分号/换行分隔的参数名黑名单，
/// 即使转发开启也不转发；<c>sign</c> 恒不转发（本站签名不应泄漏给上游）。</item>
/// </list>
/// <para>
/// 转发是<b>覆盖</b>语义：直链里已有同名参数时用请求值替换，而不是追加重复参数 ——
/// 重复参数会让上游按第一个值处理，看起来像「设置了却没生效」。
/// </para>
/// </remarks>
public static class DirectLinkParams
{
    /// <summary>本站签名参数名，永不下发到上游。</summary>
    public const string SignParam = "sign";

    /// <summary>转发开关的设置键。</summary>
    public const string ForwardKey = "forward_direct_link_params";

    /// <summary>转发黑名单的设置键。</summary>
    public const string IgnoreKey = "ignore_direct_link_params";

    /// <summary>黑名单分隔符：逗号（中英文）、分号、换行、空白。</summary>
    private static readonly char[] Separators = [',', '，', ';', '；', '\n', '\r', ' ', '\t'];

    /// <summary>
    /// 把请求参数按规则合入直链。未开启转发时原样返回，保证默认行为不变。
    /// </summary>
    /// <param name="url">驱动给出的直链（可能自带查询串与 fragment）。</param>
    /// <param name="requestParams">当前 HTTP 请求的查询参数（值可为 null/空，空值会被丢弃）。</param>
    /// <param name="forward">是否启用转发（<c>forward_direct_link_params</c>）。</param>
    /// <param name="ignoreList">黑名单原文（<c>ignore_direct_link_params</c>），可为空。</param>
    /// <returns>合入参数后的直链；无变化时返回原串。</returns>
    public static string Build(
        string url,
        IEnumerable<KeyValuePair<string, string?>> requestParams,
        bool forward,
        string? ignoreList)
    {
        if (!forward || string.IsNullOrEmpty(url)) return url;

        var ignored = ParseIgnored(ignoreList);

        // fragment 不参与拼接，最后原样贴回。
        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var withoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;

        var queryIndex = withoutFragment.IndexOf('?');
        var path = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;
        var existing = queryIndex >= 0 ? withoutFragment[(queryIndex + 1)..] : string.Empty;

        // 直链自带的参数保持原样（已是百分号编码，重新编码会变成二次编码）。
        var segments = new List<string>();
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in existing.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            indexByName[DecodeName(segment)] = segments.Count;
            segments.Add(segment);
        }

        foreach (var (name, value) in requestParams)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (ignored.Contains(name)) continue;
            // 空值没有转发价值，还会被上游当成「显式清空该参数」。
            if (string.IsNullOrEmpty(value)) continue;

            var segment = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
            if (indexByName.TryGetValue(name, out var index)) segments[index] = segment;
            else
            {
                indexByName[name] = segments.Count;
                segments.Add(segment);
            }
        }

        var query = segments.Count == 0 ? string.Empty : "?" + string.Join("&", segments);
        return path + query + fragment;
    }

    /// <summary>
    /// 解析黑名单：<c>sign</c> 恒在内；条目大小写不敏感。
    /// </summary>
    public static HashSet<string> ParseIgnored(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SignParam };
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var item in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = item.Trim().TrimStart('?');
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    /// <summary>取 <c>name=value</c> 片段里的参数名并解码；解码失败按原文处理（不抛）。</summary>
    private static string DecodeName(string segment)
    {
        var eq = segment.IndexOf('=');
        var name = eq >= 0 ? segment[..eq] : segment;
        try { return Uri.UnescapeDataString(name); }
        catch (UriFormatException) { return name; }
    }

    /// <summary>
    /// 读取两个设置并返回「合入请求参数后的直链」。仅在请求确实带额外参数时才查设置表。
    /// </summary>
    /// <param name="url">驱动给出的直链。</param>
    /// <param name="query">当前请求的查询参数（含 <c>sign</c> 也无妨，它恒被过滤）。</param>
    /// <param name="settings">设置服务。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<string> ApplyAsync(
        string url,
        IEnumerable<KeyValuePair<string, string?>> query,
        ISettingService settings,
        CancellationToken ct = default)
    {
        var parameters = query as IReadOnlyCollection<KeyValuePair<string, string?>>
                         ?? query.ToList();

        // 只剩 sign（或压根没有 query）时，转发与否结果都一样，省掉两次查库。
        if (!parameters.Any(p => !string.IsNullOrEmpty(p.Key)
                                 && !string.Equals(p.Key, SignParam, StringComparison.OrdinalIgnoreCase)))
            return url;

        var forward = await settings.GetAsync(ForwardKey, ct);
        if (forward.Code != 200 || !IsTrue(forward.Data?.Value)) return url;

        var ignore = await settings.GetAsync(IgnoreKey, ct);
        return Build(url, parameters, forward: true, ignore.Data?.Value);
    }

    /// <summary>设置值是否表示「开启」（兼容 true/1/yes/on）。</summary>
    private static bool IsTrue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }
}
