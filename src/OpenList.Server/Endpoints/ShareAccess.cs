using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 分享码鉴权：把 <c>Authorization: &lt;shareCode&gt;</c> 解析成一个「受限访客」。
///
/// 为什么需要它：后端此前只有 <c>/sd/{sid}</c>（单文件流式下载），没有「按分享码列目录」的能力，
/// 所以分享只能给直链、无法浏览。这里补上 Go 原实现里 <c>Auth(true)</c> + <c>FsAndShare</c> 组的语义：
/// 匿名请求若带分享码，就换成一个 <c>BasePath = 分享路径</c> 的访客用户，后续
/// <c>JoinGuard</c> 会把请求路径拼到分享根之下，天然限制了越界访问。
///
/// 判定「是不是分享码」的规则：**未登录** + <c>Authorization</c> 非空 + **不含 '.'**。
/// 本项目 JWT 固定是三段式（`a.b.c`），所以无点即视为分享码；已登录请求直接跳过，
/// 避免把普通用户的 token 误当分享码。
/// </summary>
public static class ShareAccess
{
    /// <summary>
    /// 尝试解析分享码。
    /// </summary>
    /// <param name="ctx">当前请求（读取 Authorization 头）。</param>
    /// <param name="shares">分享服务。</param>
    /// <param name="authenticated">请求是否已通过 JWT 认证（已认证则不做分享解析）。</param>
    /// <param name="password">分享密码（复用请求体里的 <c>password</c> 字段）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// <c>(null, null)</c> 表示这不是分享访问，按原逻辑继续；
    /// <c>(error, null)</c> 表示分享校验失败（密码错、已禁用、已过期、超次数），直接返回该错误；
    /// <c>(null, user)</c> 表示命中分享，应改用该受限用户。
    /// </returns>
    public static async Task<(IResult? Error, User? User)> ResolveAsync(
        HttpContext ctx,
        ISharingService shares,
        bool authenticated,
        string? password,
        CancellationToken ct)
    {
        if (authenticated) return (null, null);

        var raw = ctx.Request.Headers.Authorization.ToString().Trim();
        if (raw.Length == 0 || raw.Contains('.')) return (null, null);

        var verify = await shares.VerifyAsync(raw, string.IsNullOrEmpty(password) ? null : password, ct);
        if (verify.Code != RespCodes.Ok || verify.Data is null) return (Results.Ok(verify), null);

        var share = verify.Data;
        var root = share.Path.TrimEnd('/');
        return (null, new User
        {
            // Id=0 表示匿名来源；Role=Guest 不携带任何权限位，写操作会因权限位不足被拒。
            Id = 0,
            Username = $"share/{share.ShareCode}",
            Role = UserRole.Guest,
            BasePath = string.IsNullOrEmpty(root) ? "/" : root,
            Permission = 0,
        });
    }
}
