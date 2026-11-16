using System.Security.Claims;
using OpenList.Core.Models;

namespace OpenList.Core.Security;

/// <summary>
/// 从 <see cref="ClaimsPrincipal"/>（JWT 声明）中提取用户身份的扩展方法。
/// <para>
/// 放在 Core 而不是 Server：任何模块（媒体域端点、未来的独立模块项目）都要把
/// HTTP 层的 <see cref="ClaimsPrincipal"/> 映射成服务层使用的 <see cref="User"/>，
/// 若留在 Server 里，独立模块就必须反向引用宿主程序集，模块化边界会被打破。
/// </para>
/// </summary>
public static class AuthClaimsExtensions
{
    /// <summary>从声明中解析用户 Id（ClaimTypes.NameIdentifier），解析失败返回 null。</summary>
    public static long? UserId(this ClaimsPrincipal principal)
    {
        var v = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(v, out var id) ? id : null;
    }

    /// <summary>
    /// 是否已登录（持有有效身份声明）。单用户产品只有一个账号，故「已登录」等价于「管理员」。
    /// </summary>
    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.IsAuthenticated();

    /// <summary>当前请求是否携带可用的身份（匿名请求返回 false）。</summary>
    public static bool IsAuthenticated(this ClaimsPrincipal principal)
        => principal.Identity?.IsAuthenticated == true && principal.UserId() is > 0;

    /// <summary>
    /// 把声明映射为最小 <see cref="User"/>，供服务层做权限校验。
    /// 匿名请求得到 <c>Id = 0</c> 的访客（服务层据此判定「未登录」）。
    /// </summary>
    public static User ToUser(this ClaimsPrincipal principal) => new()
    {
        Id = principal.UserId() ?? 0,
        Username = principal.Identity?.Name ?? string.Empty,
    };
}
