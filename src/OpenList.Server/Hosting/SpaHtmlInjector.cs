namespace OpenList.Server.Hosting;

/// <summary>
/// SPA 外壳（<c>index.html</c>）的字符串注入。
///
/// 用途是站点设置里的 <c>customize_head</c> / <c>customize_body</c>：管理员想塞统计脚本、
/// 自定义 meta 或 body 末尾的挂件时，不必重新构建前端。
///
/// 抽成独立类型只为一件事：**可测**。注入逻辑放在 <c>Program.cs</c> 的顶层语句里就只能靠
/// 起一个真服务器验证，而「找不到 <c>&lt;/head&gt;</c> 时怎么办」恰恰是最容易出错的分支。
/// </summary>
public static class SpaHtmlInjector
{
    /// <summary>
    /// 把 <paramref name="snippet"/> 插到 <paramref name="marker"/> 之前（大小写不敏感）。
    ///
    /// 找不到标记时**追加到末尾**而不是丢弃：自定义脚本静默失效比排版偏移难排查得多。
    /// 片段为空时原样返回，避免产生无意义的字符串拷贝。
    /// </summary>
    public static string InjectBefore(string html, string marker, string snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return html;
        var index = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? html + snippet : html.Insert(index, snippet);
    }

    /// <summary>
    /// 按设置一次性注入 head 与 body 片段。
    ///
    /// 先插 head 再插 body：body 片段里的字符串如果恰好包含 <c>&lt;/head&gt;</c>
    /// （例如脚本里拼 HTML），不会影响 head 的定位。
    /// </summary>
    public static string Apply(string html, string? customizeHead, string? customizeBody)
    {
        var result = InjectBefore(html, "</head>", customizeHead ?? "");
        return InjectBefore(result, "</body>", customizeBody ?? "");
    }
}
