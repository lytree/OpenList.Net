using System.Security.Claims;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Core.Security;

/// <summary>
/// 从 <see cref="ClaimsPrincipal"/>（JWT 声明）中提取用户身份与角色的扩展方法。
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

    /// <summary>从角色声明解析用户角色，非法值回退为 Guest。</summary>
    public static UserRole UserRole(this ClaimsPrincipal principal)
    {
        var v = principal.FindFirst(ClaimTypes.Role)?.Value;
        return int.TryParse(v, out var r) ? (UserRole)r : Models.UserRole.Guest;
    }

    /// <summary>是否为管理员角色。</summary>
    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.UserRole() == Models.UserRole.Admin;

    /// <summary>
    /// 把声明映射为最小 <see cref="User"/>，供服务层做权限校验。
    /// <para>
    /// 除身份三件套外还带出 <see cref="User.BasePath"/> 与 <see cref="User.Permission"/>：
    /// 它们由 <c>JwtService.IssueToken</c> 签进 token，是目录隔离（BasePath）与权限位判定的唯一来源。
    /// 之前没带出来，导致服务层看到的用户永远是 BasePath=null、Permission=0 ——
    /// 表现为「普通用户被当成零权限、且 BasePath 约束完全不生效」。
    /// </para>
    /// <para>
    /// 匿名请求（虚拟 Guest，<c>Id=0</c>）没有这两个 claim：BasePath 退回 null（等价于根目录、不约束），
    /// Permission 退回 0。改造前签发的旧 token 同理，行为与改造前一致，因此不存在兼容性回归。
    /// </para>
    /// </summary>
    public static User ToUser(this ClaimsPrincipal principal)
    {
        var id = principal.UserId() ?? 0;
        var basePath = principal.FindFirst(OpenListClaims.BasePath)?.Value;
        var permission = principal.FindFirst(OpenListClaims.Permission)?.Value;
        return new User
        {
            Id = id,
            Username = principal.Identity?.Name ?? string.Empty,
            Role = principal.UserRole(),
            BasePath = string.IsNullOrEmpty(basePath) ? null : basePath,
            Permission = int.TryParse(permission, out var p) ? p : 0,
        };
    }
}
