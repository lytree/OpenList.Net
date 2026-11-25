using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 「想要」（库外作品清单）服务。
/// <para>
/// 与 <see cref="UserMediaState.WatchLater"/> 的区别是**有没有文件**：稍后看是已入库条目上的
/// 个人标记，本服务管的是「库里还没有、我想看」的作品，它从关注更新流或手工添加进入，
/// 后续走「找资源 → 下载 → 扫描入库」，入库后 <see cref="MediaWant.ItemId"/> 被回填。
/// </para>
/// <para>
/// 单用户产品：清单是全站事实，没有 UserId 列。读与写都要求已登录 —— 它不是媒体门面，
/// 而是「我的待看队列」，游客看到一条点不进去的清单只会困惑。
/// </para>
/// </summary>
public interface IMediaWantService
{
    /// <summary>
    /// 加入想看（幂等：同一 (媒体类型, 外部 id) 只留一行，重复调用返回已有行并补齐空字段）。
    /// 作品已经在库里时立即回填 <see cref="MediaWant.ItemId"/>。
    /// </summary>
    Task<Resp<MediaWant>> AddAsync(AddMediaWantReq req, User user, CancellationToken ct = default);

    /// <summary>
    /// 分页列出想看。<paramref name="includeIngested"/> 为 false（默认）时只列**还没入库**的
    /// —— 已入库的作品在媒体库里已经有了，留在「想看」页只是噪音。
    /// </summary>
    Task<Resp<PageResp<MediaWant>>> ListAsync(int page, int perPage, bool includeIngested, User user, CancellationToken ct = default);

    /// <summary>删除一条想看（连同它的下载记录；幂等）。</summary>
    Task<Resp> RemoveAsync(long id, User user, CancellationToken ct = default);

    /// <summary>
    /// 把「已入库」的作品回填到想看行（<see cref="MediaWant.ItemId"/>）。
    /// 扫描/刮削随时可能把作品入库，所以这是幂等的补齐动作，下载轮询每轮都会调一次。
    /// </summary>
    /// <param name="wantId">只补这一条；为空则补全部未入库的行。</param>
    /// <returns>本次回填的行数。</returns>
    Task<int> LinkIngestedAsync(long? wantId = null, CancellationToken ct = default);
}

/// <summary>加入想看的请求（字段与关注更新流的一条作品同形）。</summary>
public sealed class AddMediaWantReq
{
    /// <summary>数据源作品 id（TMDB id 字符串）。</summary>
    [JsonPropertyName("external_id")]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary><c>movie</c> / <c>tv</c>；缺省按电影处理。</summary>
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "movie";

    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>上映/首播年份，可为空。</summary>
    public int? Year { get; set; }

    /// <summary>海报路径（TMDB 相对路径或外链）。</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>一句简介。</summary>
    public string? Overview { get; set; }

    /// <summary>上映/首播日期 <c>YYYY-MM-DD</c>，可为空。</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
}
