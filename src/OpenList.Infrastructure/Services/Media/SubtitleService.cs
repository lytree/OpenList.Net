using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services.Media;

/// <summary>
/// 字幕服务：汇总所有已配置的字幕 provider 搜索结果，并把选中的字幕写回媒体目录。
/// 字幕文件名与视频同名（<c>影片.zh-cn.srt</c>），Jellyfin/Kodi/播放器均可自动加载。
/// </summary>
public sealed class SubtitleService : ISubtitleService
{
    private static readonly User WriterUser = new() { Id = 0, Username = "media-subtitle", Role = UserRole.Admin };

    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;
    private readonly IFileSystemService _fs;
    private readonly IEnumerable<ISubtitleProvider> _providers;

    /// <summary>创建字幕服务实例。</summary>
    public SubtitleService(OpenListDbContext db, IMediaLibraryService libraries, IFileSystemService fs,
        IEnumerable<ISubtitleProvider> providers)
    {
        _db = db;
        _libraries = libraries;
        _fs = fs;
        _providers = providers;
    }

    /// <inheritdoc />
    public async Task<Resp<List<SubtitleCandidate>>> SearchAsync(string title, int? year, string language, CancellationToken ct = default)
    {
        var results = new List<SubtitleCandidate>();
        foreach (var provider in _providers)
        {
            if (!await provider.IsConfiguredAsync(ct)) continue;
            try
            {
                results.AddRange(await provider.SearchAsync(title, year, language, ct));
            }
            catch (Exception)
            {
                // 单个字幕源失败不影响其他源。
            }
        }
        return Resp<List<SubtitleCandidate>>.Ok(results.OrderByDescending(c => c.Downloads).Take(50).ToList());
    }

    /// <inheritdoc />
    public async Task<Resp<string>> DownloadToMediaAsync(long itemId, SubtitleCandidate candidate, User user, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<string>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<string>.Fail("permission denied", 403);
        if (!user.CanWriteContent() && !user.CanManageMediaLibrary())
            return Resp<string>.Fail("permission denied", 403);

        var file = await _db.MediaFiles.AsNoTracking().Where(f => f.ItemId == itemId).OrderBy(f => f.Path).FirstOrDefaultAsync(ct);
        if (file is null) return Resp<string>.Fail("no media file for this item", RespCodes.NotFound);

        var provider = _providers.FirstOrDefault(p => p.Name == InferProvider(candidate));
        if (provider is null) return Resp<string>.Fail("subtitle provider not available", RespCodes.BadRequest);

        var bytes = await provider.DownloadAsync(candidate, ct);
        if (bytes is null || bytes.Length == 0) return Resp<string>.Fail("subtitle download failed", 502);

        var dir = file.Path[..file.Path.LastIndexOf('/')];
        var baseName = Path.GetFileNameWithoutExtension(file.Path);
        var lang = string.IsNullOrWhiteSpace(candidate.Language) ? "und" : candidate.Language.ToLowerInvariant();
        var path = $"{dir}/{baseName}.{lang}.{candidate.Format}";

        try
        {
            using var ms = new MemoryStream(bytes);
            var resp = await _fs.PutStreamAsync(path, ms, WriterUser, ct);
            return resp.Code == 200
                ? Resp<string>.Ok(path)
                : Resp<string>.Fail("failed to write subtitle file", 500);
        }
        catch (Exception ex)
        {
            return Resp<string>.Fail(ex.Message, 500);
        }
    }

    /// <summary>根据候选标识推断 provider（当前约定 id 为纯数字时属于 OpenSubtitles）。</summary>
    private static string InferProvider(SubtitleCandidate candidate)
        => long.TryParse(candidate.Id, out _) ? "opensubtitles" : "opensubtitles";
}
