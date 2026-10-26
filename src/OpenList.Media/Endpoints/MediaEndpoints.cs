namespace OpenList.Media.Endpoints;

/// <summary>
/// <c>/api/media</c> 路由组入口：只负责分组与子域挂载，具体端点分散在同目录的
/// <c>*Endpoints.cs</c> 里（库 / 条目 / 个人状态 / 播放列表 / 聚合 / 字幕）。
/// <para>
/// 分组统一 <c>RequireAuthorization()</c>：媒体域没有匿名端点。库管理、扫描、刮削的
/// 管理权限在服务层按 <see cref="OpenList.Core.Models.User.CanManageMediaLibrary"/> 校验，
/// 浏览类端点按 <c>BasePath</c> 过滤，因此这里不再逐个标注策略。
/// </para>
/// </summary>
public static class MediaEndpoints
{
    /// <summary>注册 <c>/api/media</c> 路由组（需登录）。</summary>
    public static IEndpointRouteBuilder MapMediaApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/media").WithTags("Media").RequireAuthorization();

        g.MapLibraryEndpoints();   // 库 CRUD + 扫描
        g.MapItemEndpoints();      // 条目查询 + 刮削
        g.MapStateEndpoints();     // 收藏 / 看过 / 稍后 / 标记 / 口味
        g.MapPlaylistEndpoints();  // 用户私有播放列表
        g.MapCatalogEndpoints();   // facets / stats / home / persons / tasks
        g.MapSubtitleEndpoints();  // 字幕搜索与写回

        return app;
    }
}
