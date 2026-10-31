using System.Text;

namespace OpenList.Tests;

/// <summary>
/// 手工拼装 zip 的测试辅助：<b>不</b>声明 UTF-8 标志位（bit 11），条目名直接按给定字节写入。
/// </summary>
/// <remarks>
/// <see cref="System.IO.Compression.ZipArchive"/> 写出的条目名恒为 UTF-8 且置位 bit 11，
/// 因此造不出「Windows 老工具压缩的 GBK 包」这种形态 —— 而 <c>non_efs_zip_encoding</c>
/// 处理的正是它。条目一律 stored（不压缩），CRC32 现算，保证解压时不因校验失败报错。
/// </remarks>
internal static class LegacyZipBuilder
{
    /// <summary>代码页提供程序（GBK/936 等）只需注册一次。</summary>
    static LegacyZipBuilder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>按 GBK 编码条目名、内容按 UTF-8 写入的单条目 zip。</summary>
    public static byte[] BuildWithGbkName(string entryName, string content)
        => Build((Encoding.GetEncoding(936).GetBytes(entryName), Encoding.UTF8.GetBytes(content)));

    /// <summary>通用构造：条目名原始字节 + 内容字节，多个条目按顺序写入。</summary>
    public static byte[] Build(params (byte[] Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        var central = new List<(byte[] Name, int Length, uint Crc, uint Offset)>();

        foreach (var (name, content) in entries)
        {
            var offset = (uint)ms.Position;
            var crc = Crc32(content);
            w.Write(0x04034b50u);          // 本地文件头签名
            w.Write((ushort)20);           // version needed
            w.Write((ushort)0);            // flags：0 = 不声明 UTF-8
            w.Write((ushort)0);            // method：0 = stored
            w.Write((ushort)0);            // mod time
            w.Write((ushort)0x21);         // mod date（1980-01-01）
            w.Write(crc);
            w.Write((uint)content.Length); // compressed size
            w.Write((uint)content.Length); // uncompressed size
            w.Write((ushort)name.Length);
            w.Write((ushort)0);            // extra len
            w.Write(name);
            w.Write(content);
            central.Add((name, content.Length, crc, offset));
        }

        var centralOffset = (uint)ms.Position;
        foreach (var (name, length, crc, offset) in central)
        {
            w.Write(0x02014b50u);          // 中央目录签名
            w.Write((ushort)20);           // version made by
            w.Write((ushort)20);           // version needed
            w.Write((ushort)0);            // flags
            w.Write((ushort)0);            // method
            w.Write((ushort)0);            // time
            w.Write((ushort)0x21);         // date
            w.Write(crc);
            w.Write((uint)length);
            w.Write((uint)length);
            w.Write((ushort)name.Length);
            w.Write((ushort)0);            // extra
            w.Write((ushort)0);            // comment
            w.Write((ushort)0);            // disk start
            w.Write((ushort)0);            // internal attrs
            w.Write(0u);                   // external attrs
            w.Write(offset);               // 本地头偏移
            w.Write(name);
        }

        var centralSize = (uint)ms.Position - centralOffset;
        w.Write(0x06054b50u);              // EOCD
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)central.Count);
        w.Write((ushort)central.Count);
        w.Write(centralSize);
        w.Write(centralOffset);
        w.Write((ushort)0);                // comment len
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>逐位 CRC32（ISO-HDLC）。stored 条目也要带 CRC，否则解压时会校验失败。</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
