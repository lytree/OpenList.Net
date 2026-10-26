namespace OpenList.Media.Endpoints;

/// <summary>
/// 查询串解析助手。原先每个端点里都是 <c>int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1</c>
/// 这类重复三目运算，语义上还有两个坑：空串会被当成"提供了 0"，以及前后空白未归一。
/// 统一收在这里：解析失败一律返回默认值/null，文本字段空串归一为 null（下游都按 IsNullOrWhiteSpace 判断）。
/// </summary>
internal static class MediaQuery
{
    /// <summary>读取整数，缺省或非法时返回 <paramref name="fallback"/>。</summary>
    public static int Int(IQueryCollection q, string key, int fallback)
        => int.TryParse(q[key], out var v) ? v : fallback;

    /// <summary>读取可空整数。</summary>
    public static int? IntOrNull(IQueryCollection q, string key)
        => int.TryParse(q[key], out var v) ? v : null;

    /// <summary>读取可空长整数。</summary>
    public static long? LongOrNull(IQueryCollection q, string key)
        => long.TryParse(q[key], out var v) ? v : null;

    /// <summary>读取可空浮点数。</summary>
    public static double? DoubleOrNull(IQueryCollection q, string key)
        => double.TryParse(q[key], out var v) ? v : null;

    /// <summary>读取可空布尔值（只认 "true"/"false"，与 <c>bool.TryParse</c> 一致）。</summary>
    public static bool? BoolOrNull(IQueryCollection q, string key)
        => bool.TryParse(q[key], out var v) ? v : null;

    /// <summary>读取文本参数；缺失或仅空白时返回 null，否则返回去空白后的值。</summary>
    public static string? Text(IQueryCollection q, string key)
    {
        var raw = q[key].ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>读取分页参数（<c>page</c> / <c>per_page</c>），不做范围裁剪——裁剪在服务层做。</summary>
    public static (int Page, int PerPage) Paging(IQueryCollection q, int defaultPerPage = 30)
        => (Int(q, "page", 1), Int(q, "per_page", defaultPerPage));
}
