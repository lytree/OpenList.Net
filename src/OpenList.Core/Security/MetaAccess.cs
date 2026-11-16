using System.Text.RegularExpressions;
using OpenList.Core.Models;
using OpenList.Vfs;

namespace OpenList.Core.Security;

/// <summary>
/// 路径元数据（meta）的访问判定，镜像 Go <c>server/common/check.go</c> 的
/// <c>CanAccess</c> / <c>MetaCoversPath</c>，以及 <c>internal/op/meta.go</c> 的最近 meta 查找。
/// <para>
/// 这套判定此前在端点层与搜索服务里各写了一份，导致「密码目录只挡搜索、不挡列目录/直链」。
/// 收敛到这里，生成侧（签名策略）与校验侧（fs/list、fs/get、/d/）才有同一把尺子。
/// </para>
/// <para>
/// 单用户产品下不再有按用户的白名单：隐藏规则与目录密码只约束<em>匿名访客</em>，
/// 已登录的管理员与系统内部身份一律放行。
/// </para>
/// </summary>
public static class MetaAccess
{
    /// <summary>规范化路径（镜像 Go <c>utils.FixAndCleanPath</c>）。</summary>
    public static string Normalize(string? path) => UPath.FixAndCleanPath(path);

    /// <summary>父目录（镜像 Go <c>path.Dir</c>）；根目录的父目录仍是根。</summary>
    public static string Parent(string? path)
    {
        var p = Normalize(path).TrimEnd('/');
        if (p.Length == 0) return "/";
        var slash = p.LastIndexOf('/');
        return slash <= 0 ? "/" : p[..slash];
    }

    /// <summary>末段名称（镜像 Go <c>path.Base</c>）。</summary>
    public static string Name(string? path)
    {
        var p = Normalize(path).TrimEnd('/');
        if (p.Length == 0) return "/";
        var slash = p.LastIndexOf('/');
        return slash < 0 ? p : p[(slash + 1)..];
    }

    /// <summary>判断 <paramref name="path"/> 是否位于 <paramref name="parent"/> 之下（含相等），镜像 Go <c>utils.IsSubPath</c>。</summary>
    public static bool IsSubPath(string? parent, string? path)
    {
        var p = Normalize(parent);
        var c = Normalize(path);
        if (p == c) return true;
        if (p == "/") return true;
        return c.StartsWith(p + "/", StringComparison.Ordinal);
    }

    /// <summary>镜像 Go <c>common.MetaCoversPath</c>：meta 是否覆盖给定路径（可选是否作用于子目录）。</summary>
    public static bool MetaCoversPath(string? metaPath, string? reqPath, bool applyToSubFolder)
    {
        var mp = Normalize(metaPath);
        var rp = Normalize(reqPath);
        if (string.Equals(mp, rp, StringComparison.OrdinalIgnoreCase)) return true;
        if (!applyToSubFolder) return false;
        while (rp != "/")
        {
            rp = Parent(rp);
            if (string.Equals(mp, rp, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 取「最近」的 meta：路径自身或其最近祖先（忽略 <c>PSub</c>——是否作用于子目录由
    /// <see cref="MetaCoversPath"/> 的调用决定），镜像 Go <c>op.GetNearestMeta</c>。
    /// </summary>
    public static Meta? FindNearest(IEnumerable<Meta> metas, string? path)
    {
        Meta? best = null;
        var bestLength = -1;
        foreach (var m in metas)
        {
            if (string.IsNullOrEmpty(m.Path)) continue;
            if (!MetaCoversPath(m.Path, path, true)) continue;
            if (m.Path.Length <= bestLength) continue;
            best = m;
            bestLength = m.Path.Length;
        }
        return best;
    }

    /// <summary>
    /// 综合访问判定：隐藏条目 → 免密用户 → 密码（受 <c>PSub</c> 控制是否作用于子目录）。
    /// </summary>
    /// <param name="user">当前用户；null 表示内部/系统上下文，直接放行。</param>
    /// <param name="meta">请求路径最近的 meta，可为 null。</param>
    /// <param name="reqPath">请求路径（规范化前的原始值也可）。</param>
    /// <param name="password">请求携带的目录密码，可为 null。</param>
    public static bool CanAccess(User? user, Meta? meta, string? reqPath, string? password)
    {
        if (user is null) return true;

        var path = Normalize(reqPath);

        // 隐藏规则只作用于「当前路径的父目录」，且只看最近的 meta（与 Go 注释一致）。
        if (meta is not null && !user.CanSeeHides() && !string.IsNullOrEmpty(meta.Hide)
            && MetaCoversPath(meta.Path, Parent(path), meta.HideSub))
        {
            var baseName = Name(path);
            foreach (var line in meta.Hide.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    if (Regex.IsMatch(baseName, line)) return false;
                }
                catch
                {
                    // 非法正则按「不匹配」处理，避免一条写坏的正则把整个目录锁死。
                }
            }
        }

        // 已认证身份（管理员 / 系统内部）免密访问受保护目录。
        if (user.CanAccessWithoutPassword()) return true;

        if (meta is null || string.IsNullOrEmpty(meta.Password)) return true;

        // 密码不作用于子目录时，子路径放行。
        if (!MetaCoversPath(meta.Path, path, meta.PSub)) return true;

        return string.Equals(meta.Password, password, StringComparison.Ordinal);
    }
}
