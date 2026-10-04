using System.Security.Cryptography;
using System.Text;
using OpenList.Core.Torrent;

namespace OpenList.Core.Torrent;

/// <summary>
/// 解析后的 .torrent 视图。仅暴露 parse / rapid_upload 接口所需字段（与 Go <c>pkg/torrent.Torrent</c> 字段对齐）。
/// </summary>
public sealed class TorrentInfo
{
    /// <summary>torrent 名称（单文件模式为文件名，多文件模式为目录名）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>分片大小（字节）。</summary>
    public long PieceLength { get; set; }
    /// <summary>所有分片 SHA-1 拼接（每 20 字节一项）。</summary>
    public byte[] Pieces { get; set; } = [];
    /// <summary>单文件模式下的文件大小（多文件模式为 0，请用 <see cref="TotalSize"/>）。</summary>
    public long Length { get; set; }
    /// <summary>多文件模式下的文件列表；单文件模式为空。</summary>
    public List<TorrentFile> Files { get; set; } = [];
    /// <summary>info 字典的 SHA-1 哈希（20 字节）。</summary>
    public byte[] InfoHash { get; set; } = [];
    /// <summary>tracker URL（announce），可能为空。</summary>
    public string Announce { get; set; } = string.Empty;
    /// <summary>tracker 列表（announce-list）。</summary>
    public List<List<string>> AnnounceList { get; set; } = [];
    /// <summary>注释，可能为空。</summary>
    public string Comment { get; set; } = string.Empty;
    /// <summary>创建者（"created by"），可能为空。</summary>
    public string CreatedBy { get; set; } = string.Empty;
    /// <summary>创建时间（Unix 秒），可能为 0。</summary>
    public long CreationDate { get; set; }
    /// <summary>天翼云 CAS 扩展信息（root["x-cas"]），没有时为 null。</summary>
    public CasInfo? Cas { get; set; }

    /// <summary>所有文件总大小。</summary>
    public long TotalSize => Files.Count > 0 ? Files.Sum(f => f.Length) : Length;

    /// <summary>分片数量（每片 20 字节 SHA-1）。</summary>
    public int PieceCount => Pieces.Length / 20;

    /// <summary>info_hash 的十六进制字符串（小写）。</summary>
    public string InfoHashHex
    {
        get
        {
            var sb = new StringBuilder(InfoHash.Length * 2);
            foreach (var b in InfoHash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>是否携带完整 CAS 秒传信息（file_md5 + slice_md5）。</summary>
    public bool HasCasInfo() => Cas is not null && !string.IsNullOrEmpty(Cas.FileMd5) && !string.IsNullOrEmpty(Cas.SliceMd5);

    /// <summary>
    /// 解析 .torrent 字节为 <see cref="TorrentInfo"/>，并校验 patch（pieces 长度必须为 20 倍数）。
    /// </summary>
    /// <exception cref="Bencode.BencodeFormatException">bencode 格式错误。</exception>
    /// <exception cref="InvalidTorrentException">torrent 字段缺失或不合法。</exception>
    public static TorrentInfo Parse(byte[] data)
    {
        var root = Bencode.Decode(data, out var rootEnd);
        if (root is not Bencode.DictNode rootDict)
            throw new InvalidTorrentException("root is not a dictionary");

        var t = new TorrentInfo();

        if (rootDict.Entries.TryGetValue("announce", out var announce))
            t.Announce = announce.AsString();

        if (rootDict.Entries.TryGetValue("announce-list", out var announceList)
            && announceList is Bencode.ListNode announceOuter)
        {
            foreach (var tierNode in announceOuter.Items)
            {
                if (tierNode is Bencode.ListNode tier)
                {
                    var urls = new List<string>();
                    foreach (var item in tier.Items)
                    {
                        var s = item.AsString();
                        if (!string.IsNullOrEmpty(s)) urls.Add(s);
                    }
                    if (urls.Count > 0) t.AnnounceList.Add(urls);
                }
            }
        }

        if (rootDict.Entries.TryGetValue("comment", out var comment))
            t.Comment = comment.AsString();

        if (rootDict.Entries.TryGetValue("created by", out var createdBy))
            t.CreatedBy = createdBy.AsString();

        if (rootDict.Entries.TryGetValue("creation date", out var creationDate))
            t.CreationDate = creationDate.AsInt();

        // info 字典：先按 binary 取 SHA-1，再递归解析字段。
        // 注意：上游 .NET 测试中往往把整个 info 字典先 bencode 一次再作为 BytesNode 嵌入，
        // 所以 info 字段可能是 BytesNode（嵌入的 bencode）或直接的 DictNode（已解码）。
        // 两种形态都要支持。
        Bencode.DictNode infoDict;
        if (rootDict.Entries.TryGetValue("info", out var infoNode))
        {
            if (infoNode is Bencode.DictNode d)
            {
                infoDict = d;
            }
            else if (infoNode is Bencode.BytesNode infoBytes)
            {
                var inner = Bencode.Decode(infoBytes.Value);
                if (inner is not Bencode.DictNode innerDict)
                    throw new InvalidTorrentException("info is not a dictionary");
                infoDict = innerDict;
            }
            else
            {
                throw new InvalidTorrentException("info is missing or not a dictionary");
            }
        }
        else
        {
            throw new InvalidTorrentException("info is missing or not a dictionary");
        }

        // 重新编码 info 字典以计算 SHA-1；bencode 键的字典序由 EncodeNode 保证。
        t.InfoHash = SHA1.HashData(Bencode.Encode(infoDict));

        if (infoDict.Entries.TryGetValue("name", out var name))
            t.Name = name.AsString();

        if (infoDict.Entries.TryGetValue("piece length", out var pieceLen))
            t.PieceLength = pieceLen.AsInt();

        if (infoDict.Entries.TryGetValue("pieces", out var pieces) && pieces.AsBytes() is { } p)
            t.Pieces = p;

        if (infoDict.Entries.TryGetValue("length", out var length))
            t.Length = length.AsInt();

        if (infoDict.Entries.TryGetValue("md5sum", out var md5))
            t.Md5Sum = md5.AsString();

        // 多文件模式：files 是列表，每项是 { length, path, md5sum? }。
        if (infoDict.Entries.TryGetValue("files", out var filesNode)
            && filesNode is Bencode.ListNode filesList)
        {
            foreach (var fileNode in filesList.Items)
            {
                if (fileNode is not Bencode.DictNode fd) continue;
                var file = new TorrentFile { Length = fd.Entries.TryGetValue("length", out var l) ? l.AsInt() : 0 };
                if (fd.Entries.TryGetValue("path", out var path) && path is Bencode.ListNode pList)
                {
                    foreach (var seg in pList.Items)
                    {
                        var s = seg.AsString();
                        if (!string.IsNullOrEmpty(s)) file.Path.Add(s);
                    }
                }
                if (fd.Entries.TryGetValue("md5sum", out var fm))
                    file.Md5Sum = fm.AsString();
                t.Files.Add(file);
            }
        }

        // 校验 pieces 长度为 20 的整数倍（Go validateParsedTorrent）。
        if (t.Pieces.Length % 20 != 0)
            throw new InvalidTorrentException("torrent pieces 数据无效：长度必须为 20 的整数倍");

        // CAS 扩展（root["x-cas"]，必须放在 info 之外才能被服务端正确处理）。
        if (rootDict.Entries.TryGetValue("x-cas", out var casNode)
            && casNode is Bencode.DictNode casDict)
        {
            var cas = new CasInfo();
            if (casDict.Entries.TryGetValue("cloud", out var cloud)) cas.Cloud = cloud.AsString();
            if (casDict.Entries.TryGetValue("file_md5", out var fileMd5)) cas.FileMd5 = fileMd5.AsString();
            if (casDict.Entries.TryGetValue("slice_md5", out var sliceMd5)) cas.SliceMd5 = sliceMd5.AsString();
            if (casDict.Entries.TryGetValue("slice_size", out var sliceSize)) cas.SliceSize = sliceSize.AsInt();
            if (casDict.Entries.TryGetValue("slice_md5s", out var sliceMd5s)
                && sliceMd5s is Bencode.ListNode sliceList)
            {
                foreach (var item in sliceList.Items)
                {
                    var s = item.AsString();
                    if (!string.IsNullOrEmpty(s)) cas.SliceMd5s.Add(s);
                }
            }
            t.Cas = cas;
        }

        // rootEnd 当前未使用 —— 保留以备扩展（例如校验 announce-list 紧跟 announce 的字节位置）。
        _ = rootEnd;

        return t;
    }

    /// <summary>info["md5sum"]，单文件模式可选。</summary>
    public string Md5Sum { get; set; } = string.Empty;
}

/// <summary>torrent 中单个文件条目（多文件模式）。</summary>
public sealed class TorrentFile
{
    /// <summary>文件大小。</summary>
    public long Length { get; set; }
    /// <summary>路径段（相对种子根目录）。</summary>
    public List<string> Path { get; set; } = [];
    /// <summary>文件 MD5（可选）。</summary>
    public string Md5Sum { get; set; } = string.Empty;
    /// <summary>拼接后的路径字符串（path 段以 '/' 连接）。</summary>
    public string PathString => string.Join('/', Path);
}

/// <summary>天翼云 CAS 秒传所需信息（root["x-cas"]）。</summary>
public sealed class CasInfo
{
    /// <summary>整文件 MD5（大写十六进制）。</summary>
    public string FileMd5 { get; set; } = string.Empty;
    /// <summary>分片 MD5 的摘要（大写十六进制）。</summary>
    public string SliceMd5 { get; set; } = string.Empty;
    /// <summary>每片 SHA-1（实际是 MD5）哈希（大写十六进制）。</summary>
    public List<string> SliceMd5s { get; set; } = [];
    /// <summary>分片大小（字节）。</summary>
    public long SliceSize { get; set; }
    /// <summary>云盘类型标识（"189"）。</summary>
    public string Cloud { get; set; } = string.Empty;
}

/// <summary>非 bencode 而是 torrent 语义错误（缺字段、字段不合法）。</summary>
public sealed class InvalidTorrentException : Exception
{
    /// <summary>构造异常。</summary>
    public InvalidTorrentException(string message) : base(message) { }
}