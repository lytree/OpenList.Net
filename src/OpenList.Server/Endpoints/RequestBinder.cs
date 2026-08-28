using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 请求绑定辅助类，用于支持多 HTTP 方法注册的路由（对齐 Go 的 g.Any）。
/// 带 JSON 请求体的 POST/PUT 请求将 body 反序列化为 DTO；
/// GET/HEAD 请求则从查询字符串绑定 DTO 的公共属性（遵循 JsonPropertyName）。
/// 未提供的字段保持默认值，与 Go 的 gin 绑定语义一致。
/// </summary>
internal static class RequestBinder
{
    /// <summary>
    /// 优先尝试从请求体反序列化 DTO；无有效 body 时回退到查询字符串绑定。
    /// </summary>
    public static async Task<T> BodyOrQueryAsync<T>(HttpContext ctx, CancellationToken ct = default) where T : new()
    {
        var method = ctx.Request.Method;
        // 判定是否存在请求体：显式 Content-Length > 0，或方法非 GET/HEAD。
        var hasBody = ctx.Request.ContentLength > 0 ||
                      (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase));
        if (hasBody)
        {
            try
            {
                var dto = await ctx.Request.ReadFromJsonAsync<T>(ct);
                if (dto is not null) return dto;
            }
            catch { /* 解析失败时回退到默认值 */ }
            return new T();
        }
        return BindQuery<T>(ctx);
    }

    /// <summary>
    /// 从查询字符串将 DTO 的公共可写属性逐项绑定为对应类型的值。
    /// 属性名优先取 JsonPropertyName，其次取属性名本身。
    /// </summary>
    public static T BindQuery<T>(HttpContext ctx) where T : new()
    {
        var dto = new T();
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
            var name = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name;
            var raw = ctx.Request.Query[name].ToString();
            if (raw.Length == 0) continue;
            // 解包可空类型，以便按基础类型解析。
            var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            try
            {
                if (target == typeof(string)) prop.SetValue(dto, raw);
                else if (target == typeof(int)) prop.SetValue(dto, int.Parse(raw));
                else if (target == typeof(long)) prop.SetValue(dto, long.Parse(raw));
                else if (target == typeof(bool)) prop.SetValue(dto, bool.Parse(raw));
                else if (target == typeof(double)) prop.SetValue(dto, double.Parse(raw));
            }
            catch { /* 无法解析的值忽略，保持默认值 */ }
        }
        return dto;
    }
}
