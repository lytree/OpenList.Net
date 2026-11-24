namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 创作者「作品流」数据源（可插拔）：给出一位创作者在数据源里的**全部**作品，
/// 由 <see cref="IMediaFollowService"/> 与库里已有的关注条目做差集，得出「新作品」。
/// <para>
/// provider 只负责「这个人名下有什么」，不关心哪些已经见过、哪些已入库 ——
/// 差集与落库都在服务层，这样之后挂 RSS 连接器时只需再实现一个 provider。
/// 首个实现是 TMDB <c>person/{id}/combined_credits</c>。
/// </para>
/// </summary>
public interface IPersonFeedProvider
{
    /// <summary>
    /// 取指定创作者的全部作品（演职员合并，同一作品只出现一次）。
    /// 未配置或数据源失败时抛异常，由调用方记到 <see cref="MediaFollow.LastError"/>。
    /// </summary>
    /// <param name="tmdbPersonId">创作者的 TMDB person id。</param>
    /// <param name="ct">取消令牌。</param>
    Task<IReadOnlyList<PersonFeedWork>> GetWorksAsync(string tmdbPersonId, CancellationToken ct = default);
}

/// <summary>创作者名下的一部作品（数据源快照）。</summary>
public sealed class PersonFeedWork
{
    /// <summary>作品类型：<c>movie</c> / <c>tv</c>。</summary>
    public string MediaType { get; set; } = string.Empty;
    /// <summary>作品在数据源里的 id（TMDB id 字符串，与 <see cref="MediaItem.TmdbId"/> 同形）。</summary>
    public string ExternalId { get; set; } = string.Empty;
    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>首次上映/播出日期（数据源给的原样字符串 <c>YYYY-MM-DD</c>，可为空）。</summary>
    public string? ReleaseDate { get; set; }
    /// <summary>海报路径（数据源内相对路径或外链，可为空）。</summary>
    public string? PosterPath { get; set; }
    /// <summary>该人物在这部作品里的角色/职位（actor 的 character、director 的 <c>Director</c>）。</summary>
    public string? Role { get; set; }
}
