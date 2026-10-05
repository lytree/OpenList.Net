using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Torrent;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// torrent 解析 + 秒传服务。镜像 Go 的 <c>pkg/torrent</c> + <c>handles/torrent.go</c> 行为：
/// <list type="bullet">
/// <item>解析阶段只读 bencode，不发起网络请求；</item>
/// <item>秒传阶段按 Go 的设计：只有目标驱动实现 CAS 秒传能力才有效，否则返回 501。</item>
/// </list>
/// </summary>
public sealed class TorrentService : ITorrentService
{
    private readonly MountService _mount;

    /// <summary>Base64 输入长度上限（≈14MB），对应 ~10MB 解码后大小（Go <c>maxTorrentBase64Len</c>）。</summary>
    public const int MaxBase64Len = 14 * 1024 * 1024;

    /// <summary>构造 torrent 服务实例。</summary>
    public TorrentService(MountService mount) => _mount = mount;

    /// <inheritdoc />
    public async Task<Resp<TorrentParsedResp>> ParseAsync(string torrentBase64, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(torrentBase64))
            return Resp<TorrentParsedResp>.Fail("torrent_data is required", RespCodes.BadRequest);
        if (torrentBase64.Length > MaxBase64Len)
            return Resp<TorrentParsedResp>.Fail("torrent data too large (max 10MB)", RespCodes.BadRequest);

        byte[] raw;
        try { raw = Convert.FromBase64String(torrentBase64); }
        catch (Exception ex) { return Resp<TorrentParsedResp>.Fail($"invalid base64: {ex.Message}", RespCodes.BadRequest); }

        return await Task.FromResult(ParseInternal(raw));
    }

    /// <inheritdoc />
    public async Task<Resp<TorrentUploadedResp>> UploadParseAsync(byte[] torrentBytes, CancellationToken ct = default)
    {
        if (torrentBytes is null || torrentBytes.Length == 0)
            return Resp<TorrentUploadedResp>.Fail("torrent file is empty", RespCodes.BadRequest);
        if (torrentBytes.Length > 10 * 1024 * 1024)
            return Resp<TorrentUploadedResp>.Fail("torrent file too large (max 10MB)", RespCodes.BadRequest);

        var parsed = ParseInternal(torrentBytes);
        if (parsed.Code != 200)
            return Resp<TorrentUploadedResp>.Fail(parsed.Message, parsed.Code);

        return await Task.FromResult(Resp<TorrentUploadedResp>.Ok(new TorrentUploadedResp
        {
            Info = parsed.Data!,
            TorrentData = Convert.ToBase64String(torrentBytes),
        }));
    }

    /// <inheritdoc />
    public async Task<Resp<TorrentRapidUploadResp>> RapidUploadAsync(string torrentBase64, string path, User user, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(torrentBase64))
            return Resp<TorrentRapidUploadResp>.Fail("torrent_data is required", RespCodes.BadRequest);
        if (string.IsNullOrEmpty(path))
            return Resp<TorrentRapidUploadResp>.Fail("path is required", RespCodes.BadRequest);
        if (!user.IsAdmin() && !user.CanWriteContent())
            return Resp<TorrentRapidUploadResp>.Fail("permission denied", RespCodes.Forbidden);

        byte[] raw;
        try { raw = Convert.FromBase64String(torrentBase64); }
        catch (Exception ex) { return Resp<TorrentRapidUploadResp>.Fail($"invalid base64: {ex.Message}", RespCodes.BadRequest); }

        TorrentInfo t;
        try { t = TorrentInfo.Parse(raw); }
        catch (Bencode.BencodeFormatException ex) { return Resp<TorrentRapidUploadResp>.Fail($"parse torrent failed: {ex.Message}", RespCodes.BadRequest); }
        catch (InvalidTorrentException ex) { return Resp<TorrentRapidUploadResp>.Fail(ex.Message, RespCodes.BadRequest); }

        if (!t.HasCasInfo())
            return Resp<TorrentRapidUploadResp>.Fail("torrent 不包含 CAS 扩展信息，无法秒传", RespCodes.BadRequest);

        // Go 的实现把 CAS 秒传的具体执行委托给 189PC 驱动（*_189pc.Cloud189PC）。
        // 当前 .NET 暂无天翼云 PC 驱动，因此探测目标驱动是否提供 CAS 秒传能力：
        // 任何具备 IFileSystemRapidUploader（占位接口）的驱动都可以接收；目前没人实现，
        // 故统一返回 501，与 Go 中「目标存储不是天翼云PC驱动」错误同语义。
        // 即使路径下找不到任何驱动，也返回 501 —— 调用方关心的是"是否支持 CAS 秒传"，
        // 而非"驱动是否存在"。
        var driver = _mount.FindDriverForPath(path);
        _ = driver; // 当前未使用，留作未来扩展。
        return Resp<TorrentRapidUploadResp>.Fail("目标存储不是天翼云PC驱动，不支持 CAS 秒传", RespCodes.NotImplemented);
    }

    /// <summary>解析 byte → 响应。错误封装。</summary>
    private static Resp<TorrentParsedResp> ParseInternal(byte[] raw)
    {
        TorrentInfo t;
        try { t = TorrentInfo.Parse(raw); }
        catch (Bencode.BencodeFormatException ex)
        {
            return Resp<TorrentParsedResp>.Fail($"parse torrent failed: {ex.Message}", RespCodes.BadRequest);
        }
        catch (InvalidTorrentException ex)
        {
            return Resp<TorrentParsedResp>.Fail(ex.Message, RespCodes.BadRequest);
        }

        var resp = new TorrentParsedResp
        {
            Name = t.Name,
            TotalSize = t.TotalSize,
            PieceLength = t.PieceLength,
            PieceCount = t.PieceCount,
            InfoHash = t.InfoHashHex,
            HasCas = t.HasCasInfo(),
        };

        if (t.Files.Count > 0)
        {
            foreach (var f in t.Files)
                resp.Files.Add(new TorrentFileInfo { Path = f.PathString, Size = f.Length });
        }
        else
        {
            resp.Files.Add(new TorrentFileInfo { Path = t.Name, Size = t.Length });
        }

        if (t.HasCasInfo() && t.Cas is not null)
        {
            resp.Cas = new CasInfoResp
            {
                FileMd5 = t.Cas.FileMd5,
                SliceMd5 = t.Cas.SliceMd5,
                SliceSize = t.Cas.SliceSize,
                Cloud = t.Cas.Cloud,
            };
        }
        return Resp<TorrentParsedResp>.Ok(resp);
    }
}