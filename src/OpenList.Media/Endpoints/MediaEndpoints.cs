namespace OpenList.Media.Endpoints;

/// <summary>
/// <c>/api/media</c> 路由组入口：只负责分组与子域挂载，具体端点分散在同目录的
/// <c>*Endpoints.cs</c> 里（库 / 条目 / 个人状态 / 播放列表 / 聚合 / 字幕）。
/// <para>
/// 分组整体 <c>RequireAuthorization()</c>，**浏览类只读端点逐个 <c>AllowAnonymous</c>**：
/// 媒体库是站点的公开门面，未登录的游客也能进去看（默认落地页就是 <c>/media</c>），
/// 而个人状态（收藏/进度/观看会话）与所有写操作（建库、扫描、刮削、播放列表、字幕写回）
/// 仍然需要登录。这与 <c>FsEndpoints</c> 的做法一致：组上挂要求，端点里开例外。
/// </para>
/// <para>
/// 匿名请求的身份是 <c>ClaimsPrincipal.ToUser()</c> 产出的 Guest（Id=0、BasePath=null），
/// 因此游客**能看全部媒体库**；登录用户仍按自己的 BasePath 过滤。库管理、扫描、刮削的
/// 管理权限在服务层按 <see cref="OpenList.Core.Models.User.CanManageMediaLibrary"/> 校验。
/// </para>
/// </summary>
public static class MediaEndpoints
{
    /// <summary>注册 <c>/api/media</c> 路由组（整体需登录，只读浏览端点匿名）。</summary>
    public static IEndpointRouteBuilder MapMediaApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/media").WithTags("Media").RequireAuthorization();

        g.MapLibraryEndpoints();   // 库 CRUD + 扫描
        g.MapItemEndpoints();      // 条目查询 + 刮削
        g.MapStateEndpoints();     // 收藏 / 看过 / 稍后 / 标记 / 口味
        g.MapWatchEndpoints();     // 观看会话：心跳上报 / 历史 / 逐日时长
        g.MapPlaylistEndpoints();  // 用户私有播放列表
        g.MapCatalogEndpoints();   // facets / stats / home / persons / tasks
        g.MapSubtitleEndpoints();  // 字幕搜索与写回

        return app;
    }
}
