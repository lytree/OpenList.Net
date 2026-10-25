using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 字幕源 provider（可插拔）。provider 只负责"按标题/年份搜索并下载字幕内容"，
/// 字幕文件写回媒体目录由 <see cref="ISubtitleService"/> 统一处理。
/// </summary>
public interface ISubtitleProvider
{
    /// <summary>provider 标识（如 "opensubtitles"）。</summary>
    string Name { get; }
    /// <summary>provider 是否已配置可用（如 API Key 已设置）。</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
    /// <summary>按标题/年份/语言搜索字幕候选。</summary>
    Task<List<SubtitleCandidate>> SearchAsync(string title, int? year, string language, CancellationToken ct = default);
    /// <summary>下载指定字幕内容。</summary>
    Task<byte[]?> DownloadAsync(SubtitleCandidate candidate, CancellationToken ct = default);
}

/// <summary>字幕搜索候选。</summary>
public sealed class SubtitleCandidate
{
    /// <summary>provider 内字幕 id。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>语言。</summary>
    public string Language { get; set; } = string.Empty;
    /// <summary>下载次数（用于排序）。</summary>
    public int Downloads { get; set; }
    /// <summary>格式（srt / ass / vtt）。</summary>
    public string Format { get; set; } = "srt";
    /// <summary>provider 自定义的下载标识，可为空。</summary>
    public string? DownloadToken { get; set; }
}

/// <summary>
/// 字幕服务：聚合所有已配置的字幕 provider，搜索并把选中的字幕写回媒体目录。
/// </summary>
public interface ISubtitleService
{
    /// <summary>搜索字幕（汇总所有已配置 provider 的结果，按下载量降序）。</summary>
    Task<Resp<List<SubtitleCandidate>>> SearchAsync(string title, int? year, string language, CancellationToken ct = default);
    /// <summary>下载字幕并写入媒体文件同目录（文件名与视频同名，扩展名按字幕格式）。</summary>
    Task<Resp<string>> DownloadToMediaAsync(long itemId, SubtitleCandidate candidate, User user, CancellationToken ct = default);
}
