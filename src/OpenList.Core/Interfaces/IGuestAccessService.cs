namespace OpenList.Core.Interfaces;

/// <summary>
/// 访客（未登录）访问策略。
/// <para>
/// 站点的两个门面各自有一个开关，默认值刻意不同：
/// </para>
/// <list type="bullet">
/// <item><b>媒体</b>（<c>/media</c>）默认**对游客开放** —— 它是站点的公开门面，
/// 落地页就是它，未登录先看海报墙再决定要不要登录。</item>
/// <item><b>首页</b>（<c>/files</c> 文件浏览）默认**需要登录** —— 文件树属于「找文件 / 管理」
/// 场景，把整棵挂载树裸奔在公网并不合适。管理员可以在设置里显式放开（<c>home_guest_access</c>），
/// 常见于「公开只读分享站」这种部署。</item>
/// </list>
/// <para>
/// 开关值来自设置表（<c>media_guest_access</c> / <c>home_guest_access</c>，group 13），
/// 由实现方带短 TTL 缓存；管理端保存设置后应调用 <see cref="InvalidateCache"/> 让改动立即生效。
/// </para>
/// </summary>
public interface IGuestAccessService
{
    /// <summary>未登录用户能否浏览媒体域（媒体首页 / 媒体库 / 详情 / 海报）。</summary>
    Task<bool> CanBrowseMediaAsync(CancellationToken ct = default);

    /// <summary>未登录用户能否浏览首页（文件列表 / 详情 / 目录树 / 搜索）。</summary>
    Task<bool> CanBrowseHomeAsync(CancellationToken ct = default);

    /// <summary>丢弃缓存，使下一次判定重新读取设置表。</summary>
    void InvalidateCache();
}
