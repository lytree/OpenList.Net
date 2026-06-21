using System.Security.Cryptography;
using System.Text;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Minimal torrent/bencode parser and generator.
/// Corresponds to Go: pkg/torrent package.
/// </summary>
public class TorrentService
{
    public const int DefaultPieceSize = 256 * 1024; // 256KB

    /// <summary>Decode a torrent file from raw bytes</summary>
    public TorrentInfo Decode(byte[] data)
    {
        var pos = 0;
        var dict = DecodeValue(data, ref pos) as BDict ?? throw new InvalidOperationException("invalid torrent: root is not a dictionary");

        var info = dict.GetDict("info") ?? throw new InvalidOperationException("missing info dictionary");
        var name = info.GetString("name") ?? "";
        var pieceLength = info.GetLong("piece length");
        var pieces = info.GetBytes("pieces") ?? [];

        // Extract info_hash: SHA1 of the raw bencode of the info dictionary
        var infoRaw = info.RawBytes ?? [];
        var infoHash = Convert.ToHexString(SHA1.HashData(infoRaw)).ToLowerInvariant();

        // Files
        var files = new List<TorrentFileEntry>();
        var filesList = info.GetList("files");
        long totalSize = 0;

        if (filesList != null)
        {
            // Multi-file mode
            foreach (var item in filesList.Items)
            {
                if (item is BDict fileDict)
                {
                    var length = fileDict.GetLong("length");
                    var pathList = fileDict.GetList("path");
                    var path = pathList != null
                        ? string.Join("/", pathList.Items.OfType<BString>().Select(s => s.Value))
                        : "";
                    files.Add(new TorrentFileEntry { Path = path, Size = length });
                    totalSize += length;
                }
            }
        }
        else
        {
            // Single-file mode
            totalSize = info.GetLong("length");
            files.Add(new TorrentFileEntry { Path = name, Size = totalSize });
        }

        // CAS info (optional extension)
        CASInfo? cas = null;
        var casDict = dict.GetDict("cas");
        if (casDict != null)
        {
            cas = new CASInfo
            {
                FileMD5 = casDict.GetString("file_md5") ?? "",
                SliceMD5 = casDict.GetString("slice_md5") ?? "",
                SliceSize = casDict.GetLong("slice_size"),
                Cloud = casDict.GetString("cloud") ?? ""
            };
        }

        return new TorrentInfo
        {
            Name = name,
            TotalSize = totalSize,
            PieceLength = pieceLength,
            PieceCount = pieces.Length / 20,
            InfoHash = infoHash,
            Files = files,
            HasCAS = cas != null,
            CAS = cas,
            RawData = data
        };
    }

    /// <summary>Generate a torrent from file content</summary>
    public byte[] Generate(string fileName, long fileSize, Stream content, bool withCAS = false)
    {
        var pieceSize = DefaultPieceSize;
        var pieces = new List<byte>();

        // Read file content and compute piece hashes
        var buffer = new byte[pieceSize];
        int bytesRead;
        int offset = 0;

        while ((bytesRead = content.Read(buffer, offset, pieceSize - offset)) > 0)
        {
            offset += bytesRead;
            if (offset == pieceSize)
            {
                pieces.AddRange(SHA1.HashData(buffer.AsSpan(0, pieceSize)));
                offset = 0;
            }
        }
        // Hash remaining partial piece
        if (offset > 0)
            pieces.AddRange(SHA1.HashData(buffer.AsSpan(0, offset)));

        // Build info dictionary
        var info = new BDict();
        info.Set("length", new BLong(fileSize));
        info.Set("name", new BString(fileName));
        info.Set("piece length", new BLong(pieceSize));
        info.Set("pieces", new BBytes(pieces.ToArray()));

        // Build root dictionary
        var root = new BDict();
        root.Set("info", info);

        // Encode to get raw bytes (for info_hash)
        var rootBytes = root.Encode();

        // Compute info_hash from the encoded info dict
        var infoBytes = info.Encode();
        var infoHash = Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant();

        return rootBytes;
    }

    // === Bencode types ===

    private abstract class BValue
    {
        public abstract byte[] Encode();
    }

    private class BLong : BValue
    {
        public long Value { get; set; }
        public BLong(long v) => Value = v;
        public override byte[] Encode() => Encoding.ASCII.GetBytes($"i{Value}e");
    }

    private class BString : BValue
    {
        public string Value { get; set; }
        public BString(string v) => Value = v;
        public override byte[] Encode()
        {
            var strBytes = Encoding.UTF8.GetBytes(Value);
            var prefix = Encoding.ASCII.GetBytes($"{strBytes.Length}:");
            return [..prefix, ..strBytes];
        }
    }

    private class BBytes : BValue
    {
        public byte[] Value { get; set; }
        public BBytes(byte[] v) => Value = v;
        public override byte[] Encode()
        {
            var prefix = Encoding.ASCII.GetBytes($"{Value.Length}:");
            return [..prefix, ..Value];
        }
    }

    private class BList : BValue
    {
        public List<BValue> Items { get; } = [];
        public override byte[] Encode()
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)'l');
            foreach (var item in Items)
                ms.Write(item.Encode());
            ms.WriteByte((byte)'e');
            return ms.ToArray();
        }
    }

    private class BDict : BValue
    {
        public SortedDictionary<string, BValue> Items { get; } = new(StringComparer.Ordinal);
        public byte[]? RawBytes { get; set; } // Preserved raw bytes for info_hash

        public override byte[] Encode()
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)'d');
            foreach (var (key, val) in Items)
            {
                var keyBytes = Encoding.ASCII.GetBytes($"{key.Length}:{key}");
                ms.Write(keyBytes);
                ms.Write(val.Encode());
            }
            ms.WriteByte((byte)'e');
            return ms.ToArray();
        }

        public void Set(string key, BValue value) => Items[key] = value;
        public BDict? GetDict(string key) => Items.TryGetValue(key, out var v) && v is BDict d ? d : null;
        public BList? GetList(string key) => Items.TryGetValue(key, out var v) && v is BList l ? l : null;
        public string? GetString(string key) => Items.TryGetValue(key, out var v) && v is BString s ? s.Value : null;
        public byte[]? GetBytes(string key) => Items.TryGetValue(key, out var v) && v is BBytes b ? b.Value : null;
        public long GetLong(string key) => Items.TryGetValue(key, out var v) && v is BLong i ? i.Value : 0;
    }

    // === Bencode decoder ===

    private BValue DecodeValue(byte[] data, ref int pos)
    {
        if (pos >= data.Length) throw new InvalidOperationException("unexpected end of bencode data");

        return data[pos] switch
        {
            (byte)'i' => DecodeInt(data, ref pos),
            (byte)'l' => DecodeList(data, ref pos),
            (byte)'d' => DecodeDict(data, ref pos),
            >= (byte)'0' and <= (byte)'9' => DecodeString(data, ref pos),
            _ => throw new InvalidOperationException($"unexpected byte '{(char)data[pos]}' at position {pos}")
        };
    }

    private BLong DecodeInt(byte[] data, ref int pos)
    {
        pos++; // skip 'i'
        var end = Array.IndexOf(data, (byte)'e', pos);
        if (end < 0) throw new InvalidOperationException("unterminated integer");
        var numStr = Encoding.ASCII.GetString(data, pos, end - pos);
        pos = end + 1;
        return new BLong(long.Parse(numStr));
    }

    private BString DecodeString(byte[] data, ref int pos)
    {
        var colonIdx = Array.IndexOf(data, (byte)':', pos);
        if (colonIdx < 0) throw new InvalidOperationException("unterminated string length");
        var lenStr = Encoding.ASCII.GetString(data, pos, colonIdx - pos);
        var len = int.Parse(lenStr);
        pos = colonIdx + 1;
        if (pos + len > data.Length) throw new InvalidOperationException("string data exceeds buffer");

        // Check if this looks like binary data (pieces field)
        var result = new BString(Encoding.UTF8.GetString(data, pos, len));
        pos += len;
        return result;
    }

    private BBytes DecodeStringAsBytes(byte[] data, ref int pos)
    {
        var colonIdx = Array.IndexOf(data, (byte)':', pos);
        var lenStr = Encoding.ASCII.GetString(data, pos, colonIdx - pos);
        var len = int.Parse(lenStr);
        pos = colonIdx + 1;
        var bytes = new byte[len];
        Array.Copy(data, pos, bytes, 0, len);
        pos += len;
        return new BBytes(bytes);
    }

    private BList DecodeList(byte[] data, ref int pos)
    {
        pos++; // skip 'l'
        var list = new BList();
        while (pos < data.Length && data[pos] != (byte)'e')
            list.Items.Add(DecodeValue(data, ref pos));
        pos++; // skip 'e'
        return list;
    }

    private BDict DecodeDict(byte[] data, ref int pos)
    {
        var startPos = pos;
        pos++; // skip 'd'
        var dict = new BDict();
        while (pos < data.Length && data[pos] != (byte)'e')
        {
            // Key is always a string
            var colonIdx = Array.IndexOf(data, (byte)':', pos);
            var lenStr = Encoding.ASCII.GetString(data, pos, colonIdx - pos);
            var len = int.Parse(lenStr);
            pos = colonIdx + 1;
            var key = Encoding.UTF8.GetString(data, pos, len);
            pos += len;

            // For "pieces" field, decode as raw bytes
            if (key == "pieces")
            {
                var piecesColonIdx = Array.IndexOf(data, (byte)':', pos);
                // Actually, the value starts at current pos already
                // Re-read: pos is after the key, now decode value
                var val = DecodeStringAsBytes(data, ref pos);
                dict.Set(key, val);
            }
            else
            {
                dict.Set(key, DecodeValue(data, ref pos));
            }
        }
        pos++; // skip 'e'

        // Preserve raw bytes for info_hash computation
        dict.RawBytes = new byte[pos - startPos];
        Array.Copy(data, startPos, dict.RawBytes, 0, pos - startPos);

        return dict;
    }
}

// === Public data types ===

public class TorrentInfo
{
    public string Name { get; set; } = "";
    public long TotalSize { get; set; }
    public long PieceLength { get; set; }
    public int PieceCount { get; set; }
    public string InfoHash { get; set; } = "";
    public List<TorrentFileEntry> Files { get; set; } = [];
    public bool HasCAS { get; set; }
    public CASInfo? CAS { get; set; }
    public byte[]? RawData { get; set; }
}

public class TorrentFileEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
}

public class CASInfo
{
    public string FileMD5 { get; set; } = "";
    public string SliceMD5 { get; set; } = "";
    public long SliceSize { get; set; }
    public string Cloud { get; set; } = "";
}
