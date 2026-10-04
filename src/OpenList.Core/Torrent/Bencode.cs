using System.Buffers.Binary;
using System.Text;

namespace OpenList.Core.Torrent;

/// <summary>
/// BitTorrent bencode 格式的极简解析器。覆盖 /fs/torrent/parse 与
/// /fs/torrent/rapid_upload 所需的最小子集：
/// <list type="bullet">
/// <item>整数：<c>i&lt;数字&gt;e</c>；</item>
/// <item>字节串：<c>&lt;长度&gt;:&lt;字节&gt;</c>；</item>
/// <item>列表：<c>l&lt;元素&gt;e</c>；</item>
/// <item>字典：<c>d&lt;键值对&gt;e</c>，键必须按字典序排序（torrent 强制）。</item>
/// </list>
/// 设计取舍：不支持扩展格式（list/dict 的自定义类型），因为现有 torrent 文件不依赖它们。
/// </summary>
public static class Bencode
{
    /// <summary>解码返回的对象层级。</summary>
    public abstract class Node
    {
        /// <summary>若节点是字节串，则解码为 UTF-8 字符串；否则返回空串。</summary>
        public string AsString() => this is BytesNode b ? Encoding.UTF8.GetString(b.Value) : string.Empty;
        /// <summary>若节点是字节串，则原样返回字节数组；否则返回 null。</summary>
        public byte[]? AsBytes() => this is BytesNode b ? b.Value : null;
        /// <summary>若节点是整数节点，则返回 long 值；否则返回 0。</summary>
        public long AsInt() => this is IntNode i ? i.Value : 0;
    }

    /// <summary>字节串节点。</summary>
    public sealed class BytesNode : Node
    {
        /// <summary>原始字节内容。</summary>
        public byte[] Value { get; }
        /// <summary>构造一个字节串节点（内部拷贝以避免外部修改）。</summary>
        public BytesNode(byte[] value) => Value = (byte[])value.Clone();
    }

    /// <summary>整数节点。</summary>
    public sealed class IntNode : Node
    {
        /// <summary>整数值。</summary>
        public long Value { get; }
        /// <summary>构造一个整数节点。</summary>
        public IntNode(long value) => Value = value;
    }

    /// <summary>列表节点。</summary>
    public sealed class ListNode : Node
    {
        /// <summary>子节点列表。</summary>
        public List<Node> Items { get; }
        /// <summary>构造一个列表节点。</summary>
        public ListNode(List<Node> items) => Items = items;
    }

    /// <summary>字典节点（键为 UTF-8 字符串以方便使用）。</summary>
    public sealed class DictNode : Node
    {
        /// <summary>字典键 → 节点映射。</summary>
        public Dictionary<string, Node> Entries { get; }
        /// <summary>构造一个字典节点。</summary>
        public DictNode(Dictionary<string, Node> entries) => Entries = entries;
    }

    /// <summary>
    /// 从输入的字节流解析 bencode，遇到截断或格式错误抛出 <see cref="BencodeFormatException"/>。
    /// 不读取超出末尾的字节；调用方可比较 <c>data.Length - reader.Position</c> 检查残留。
    /// </summary>
    /// <param name="data">bencode 字节流。</param>
    /// <returns>根节点。</returns>
    public static Node Decode(byte[] data)
    {
        var reader = new Reader(data);
        var node = ReadNode(reader);
        return node;
    }

    /// <summary>
    /// 同 <see cref="Decode(byte[])"/> 但会把读取游标停在根节点末尾 —— 用于计算 info 字典的 SHA-1。
    /// </summary>
    /// <param name="data">bencode 字节流。</param>
    /// <param name="length">根节点结束位置（可用于哈希原始字节）。</param>
    public static Node Decode(byte[] data, out int length)
    {
        var reader = new Reader(data);
        var node = ReadNode(reader);
        length = reader.Position;
        return node;
    }

    private static Node ReadNode(Reader r)
    {
        if (!r.HasMore) throw new BencodeFormatException("unexpected end of input");
        var c = r.PeekByte();
        return c switch
        {
            (byte)'i' => ReadInt(r),
            (byte)'l' => ReadList(r),
            (byte)'d' => ReadDict(r),
            (byte)'-' or (>= (byte)'0' and <= (byte)'9') => ReadBytes(r),
            _ => throw new BencodeFormatException($"unexpected token '{c}'"),
        };
    }

    private static IntNode ReadInt(Reader r)
    {
        r.Expect((byte)'i');
        var sb = new StringBuilder();
        while (r.HasMore)
        {
            var c = r.ReadByte();
            if (c == (byte)'e') break;
            sb.Append((char)c);
        }
        if (sb.Length == 0) throw new BencodeFormatException("empty integer");
        if (!long.TryParse(sb.ToString(), out var v)) throw new BencodeFormatException("invalid integer");
        return new IntNode(v);
    }

    private static BytesNode ReadBytes(Reader r)
    {
        var sb = new StringBuilder();
        while (r.HasMore)
        {
            var c = r.PeekByte();
            if (c == (byte)':') break;
            if (c < (byte)'0' || c > (byte)'9') throw new BencodeFormatException("invalid byte length");
            sb.Append((char)c);
            r.Position++;
        }
        if (!r.HasMore) throw new BencodeFormatException("missing ':' in bytes");
        r.Expect((byte)':');
        if (!long.TryParse(sb.ToString(), out var len) || len < 0 || len > int.MaxValue)
            throw new BencodeFormatException("byte length out of range");
        var buf = r.ReadBytes((int)len);
        return new BytesNode(buf);
    }

    private static ListNode ReadList(Reader r)
    {
        r.Expect((byte)'l');
        var items = new List<Node>();
        while (r.HasMore && r.PeekByte() != (byte)'e')
            items.Add(ReadNode(r));
        r.Expect((byte)'e');
        return new ListNode(items);
    }

    private static DictNode ReadDict(Reader r)
    {
        r.Expect((byte)'d');
        var dict = new Dictionary<string, Node>(StringComparer.Ordinal);
        while (r.HasMore && r.PeekByte() != (byte)'e')
        {
            var keyNode = ReadNode(r);
            if (keyNode is not BytesNode kb) throw new BencodeFormatException("dict key must be bytes");
            var key = Encoding.UTF8.GetString(kb.Value);
            if (!r.HasMore) throw new BencodeFormatException("dict value missing");
            dict[key] = ReadNode(r);
        }
        r.Expect((byte)'e');
        return new DictNode(dict);
    }

    /// <summary>Bencode 字节流读取游标。</summary>
    private sealed class Reader
    {
        private readonly byte[] _data;
        /// <summary>当前读取游标（0-based）。</summary>
        public int Position;
        /// <summary>构造读取器。</summary>
        public Reader(byte[] data) => _data = data;
        /// <summary>是否还有字节可读。</summary>
        public bool HasMore => Position < _data.Length;
        /// <summary>取当前位置字节但不前进。</summary>
        public byte PeekByte() => _data[Position];
        /// <summary>取当前位置字节并前进一格。</summary>
        public byte ReadByte() => _data[Position++];
        /// <summary>取接下来 <paramref name="count"/> 个字节并前进。</summary>
        public byte[] ReadBytes(int count)
        {
            if (count > _data.Length - Position) throw new BencodeFormatException("bytes truncated");
            var slice = new byte[count];
            Buffer.BlockCopy(_data, Position, slice, 0, count);
            Position += count;
            return slice;
        }
        /// <summary>断言下一个字节等于 <paramref name="expected"/>，否则抛异常。</summary>
        public void Expect(byte expected)
        {
            if (!HasMore) throw new BencodeFormatException($"expected '{expected}' but got EOF");
            var actual = _data[Position++];
            if (actual != expected) throw new BencodeFormatException($"expected '{expected}' but got '{actual}'");
        }
    }

    /// <summary>bencode 格式错误。</summary>
    public sealed class BencodeFormatException : Exception
    {
        /// <summary>构造带消息的异常。</summary>
        public BencodeFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// 编码：把 <see cref="Node"/> 重新写回 bencode 字节流。仅供 <see cref="TorrentInfo.ComputeInfoHash"/> 使用，
    /// 因此只覆盖字典/整数/字节串/列表四种基本类型（键以 UTF-8 字符串表示，按字典序排序）。
    /// </summary>
    public static byte[] Encode(Node root)
    {
        var ms = new MemoryStream();
        EncodeNode(root, ms);
        return ms.ToArray();
    }

    private static void EncodeNode(Node n, Stream s)
    {
        switch (n)
        {
            case IntNode i:
                s.WriteByte((byte)'i');
                WriteAscii(s, i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                s.WriteByte((byte)'e');
                break;
            case BytesNode b:
                WriteAscii(s, b.Value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                s.WriteByte((byte)':');
                s.Write(b.Value, 0, b.Value.Length);
                break;
            case ListNode l:
                s.WriteByte((byte)'l');
                foreach (var item in l.Items) EncodeNode(item, s);
                s.WriteByte((byte)'e');
                break;
            case DictNode d:
                s.WriteByte((byte)'d');
                // bencode 要求键按字典序；此处按原始字节序排序以与 Go 行为一致。
                foreach (var kvp in d.Entries.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var keyBytes = Encoding.UTF8.GetBytes(kvp.Key);
                    WriteAscii(s, keyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    s.WriteByte((byte)':');
                    s.Write(keyBytes, 0, keyBytes.Length);
                    EncodeNode(kvp.Value, s);
                }
                s.WriteByte((byte)'e');
                break;
        }
    }

    private static void WriteAscii(Stream s, string ascii)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        s.Write(bytes, 0, bytes.Length);
    }
}