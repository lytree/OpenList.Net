using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Media.Endpoints;
using OpenList.Media.Hosting;
using OpenList.Media.Domain.Abstractions;
using OpenList.Media.Providers;
using OpenList.Media.Services;

namespace OpenList.Media;

/// <summary>
/// 媒体域模块入口：一处完成该领域全部 DI 注册与路由挂载。
/// <para>
/// 宿主（<c>OpenList.Server/Program.cs</c>）只调用
/// <see cref="AddMediaModule"/> 与 <see cref="MapMediaModule"/> 两行，
/// 不感知任何具体服务类型；媒体域因此可以整块移除或替换而不牵动宿主。
/// </para>
/// <para>
/// 依赖方向固定为 <c>Media → Media.Domain + Infrastructure + Core</c>，
/// Infrastructure 仅依赖 <c>Media.Domain</c>（EF 映射），不反向引用本程序集。
/// </para>
/// </summary>
public static class MediaModule
{
    /// <summary>
    /// 注册媒体域服务。
    /// <para>
    /// 生命周期约定：除 <see cref="IMediaScanService"/> 外的服务都按请求作用域注册
    /// （它们直接使用 scoped 的 <c>OpenListDbContext</c>）；扫描服务是单例，
    /// 由它自己在后台任务里 <c>CreateScope</c>，避免长任务拖住请求作用域的上下文。
    /// </para>
    /// </summary>
    public static IServiceCollection AddMediaModule(this IServiceCollection services)
    {
        // 刮削与字幕走独立的命名 HttpClient，便于统一配置超时/代理。
        services.AddHttpClient("tmdb");
        services.AddHttpClient("opensubtitles");

        services.AddScoped<IMediaLibraryService, MediaLibraryService>();
        services.AddScoped<IMediaCatalogService, MediaCatalogService>();
        services.AddScoped<IMediaMetadataService, MediaMetadataService>();
        services.AddScoped<IUserMediaStateService, UserMediaStateService>();
        services.AddScoped<IMediaPlaylistService, MediaPlaylistService>();
        // 两个元数据 provider：单例解析（GetRequiredService<IMetadataProvider>）回到 TMDB；
        // MediaMetadataService 注入 IEnumerable<IMetadataProvider> 拿到全部两个，按 Name 解析。
        services.AddScoped<TmdbMetadataProvider>();
        services.AddScoped<CodeMetadataProvider>();
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<TmdbMetadataProvider>());
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<CodeMetadataProvider>());
        services.AddScoped<ISubtitleProvider, OpenSubtitlesProvider>();
        services.AddScoped<ISubtitleService, SubtitleService>();

        // 扫描执行器是 transient：无可释放资源，只借用调用方传入的 DbContext。
        services.AddTransient<MediaLibraryScanner>();
        services.AddSingleton<IMediaScanService, MediaScanService>();

        // 定时增量扫描：间隔取自设置项 media_auto_scan_interval_hours，0 表示关闭（默认）。
        services.AddHostedService<MediaAutoScanHostedService>();

        return services;
    }

    /// <summary>挂载 <c>/api/media</c> 路由组（全部需要登录，权限在端点内按用户校验）。</summary>
    public static IEndpointRouteBuilder MapMediaModule(this IEndpointRouteBuilder app)
        => MediaEndpoints.MapMediaApi(app);
}
