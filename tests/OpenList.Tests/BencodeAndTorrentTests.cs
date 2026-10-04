using System.Security.Cryptography;
using System.Text;
using OpenList.Core.Torrent;

namespace OpenList.Tests;

/// <summary>
/// Bencode 与 TorrentInfo 解析单元测试。覆盖：
/// <list type="bullet">
/// <item>基本 bencode 类型（int/bytes/list/dict）的往返；</item>
/// <item>单文件 / 多文件 torrent 的字段映射；</item>
/// <item>CAS 扩展（x-cas）的解析；</item>
/// <item>无效输入（缺字段、pieces 长度非法）抛出明确异常。</item>
/// </list>
/// </summary>
public class BencodeAndTorrentTests
{
    [Test]
    public async Task Bencode_RoundTrip_Integer()
    {
        var encoded = Bencode.Encode(new Bencode.IntNode(42));
        await Assert.That(Encoding.ASCII.GetString(encoded)).IsEqualTo("i42e");
        var node = Bencode.Decode(encoded);
        await Assert.That(node.AsInt()).IsEqualTo(42);
    }

    [Test]
    public async Task Bencode_RoundTrip_Bytes()
    {
        var encoded = Bencode.Encode(new Bencode.BytesNode(Encoding.UTF8.GetBytes("hello")));
        await Assert.That(Encoding.ASCII.GetString(encoded)).IsEqualTo("5:hello");
        var node = Bencode.Decode(encoded);
        await Assert.That(node.AsString()).IsEqualTo("hello");
    }

    [Test]
    public async Task Bencode_RoundTrip_List()
    {
        var list = new Bencode.ListNode([new Bencode.IntNode(1), new Bencode.IntNode(2), new Bencode.BytesNode("x"u8.ToArray())]);
        var encoded = Bencode.Encode(list);
        await Assert.That(Encoding.ASCII.GetString(encoded)).IsEqualTo("li1ei2e1:xe");
        var round = (Bencode.ListNode)Bencode.Decode(encoded);
        await Assert.That(round.Items.Count).IsEqualTo(3);
        await Assert.That(round.Items[0].AsInt()).IsEqualTo(1);
        await Assert.That(round.Items[2].AsString()).IsEqualTo("x");
    }

    [Test]
    public async Task Bencode_RoundTrip_Dict_SortsKeys()
    {
        var dict = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["b"] = new Bencode.IntNode(2),
            ["a"] = new Bencode.IntNode(1),
        });
        var encoded = Bencode.Encode(dict);
        // bencode 要求字典键按字典序，因此输出顺序应为 a...b...
        await Assert.That(Encoding.ASCII.GetString(encoded)).IsEqualTo("d1:ai1e1:bi2ee");
    }

    [Test]
    public async Task TorrentInfo_Parse_SingleFile()
    {
        var bytes = BuildSingleFileTorrentBytes("hello.txt", 11);
        var t = TorrentInfo.Parse(bytes);
        await Assert.That(t.Name).IsEqualTo("hello.txt");
        await Assert.That(t.Length).IsEqualTo(11);
        await Assert.That(t.Files.Count).IsEqualTo(0);
        await Assert.That(t.TotalSize).IsEqualTo(11);
        await Assert.That(t.PieceCount).IsEqualTo(1);
        // info_hash 重新编码后 SHA-1 一致。
        var expected = SHA1.HashData(BuildInfoDictBytes("hello.txt", 11, piecesLen: 20));
        await Assert.That(t.InfoHashHex).IsEqualTo(Convert.ToHexString(expected).ToLowerInvariant());
    }

    [Test]
    public async Task TorrentInfo_Parse_MultiFile_AndCas()
    {
        var bytes = BuildMultiFileTorrentBytesWithCas(pieceLength: 16384, fileSizes: [1024, 2048, 4096]);
        var t = TorrentInfo.Parse(bytes);
        await Assert.That(t.Files.Count).IsEqualTo(3);
        await Assert.That(t.TotalSize).IsEqualTo(1024 + 2048 + 4096);
        await Assert.That(t.Cas).IsNotNull();
        await Assert.That(t.Cas!.FileMd5).IsEqualTo("DEADBEEFDEADBEEFDEADBEEFDEADBEEF");
        await Assert.That(t.HasCasInfo()).IsTrue();
    }

    [Test]
    public async Task TorrentInfo_Parse_Rejects_BadPiecesLength()
    {
        var bytes = BuildSingleFileTorrentBytes("a", 11, piecesLen: 19);
        var ex = await Assert.ThrowsAsync<InvalidTorrentException>(() => Task.Run(() => TorrentInfo.Parse(bytes)));
        await Assert.That(ex!.Message).Contains("20 的整数倍");
    }

    [Test]
    public async Task TorrentInfo_Parse_Rejects_MissingInfo()
    {
        var bytes = Bencode.Encode(new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["announce"] = new Bencode.BytesNode(Encoding.UTF8.GetBytes("udp://example.invalid")),
        }));
        var ex = await Assert.ThrowsAsync<InvalidTorrentException>(() => Task.Run(() => TorrentInfo.Parse(bytes)));
        await Assert.That(ex!.Message).Contains("info");
    }

    // --- 测试夹具：手工构造 bencode 串（与 Go 测试辅助 roundtrip 等价） ---

    /// <summary>构造最简单文件 torrent（piece_length=16384, pieces=20 字节 0xAB）。</summary>
    private static byte[] BuildSingleFileTorrentBytes(string name, long length, int? piecesLen = null)
    {
        var info = BuildInfoDictBytes(name, length, piecesLen ?? 20);
        var root = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["announce"] = new Bencode.BytesNode(Encoding.UTF8.GetBytes("udp://tracker.example.invalid:80")),
            ["info"] = new Bencode.BytesNode(info),
        });
        return Bencode.Encode(root);
    }

    private static byte[] BuildInfoDictBytes(string name, long length, int piecesLen)
    {
        var pieces = new byte[piecesLen];
        for (int i = 0; i < pieces.Length; i++) pieces[i] = 0xAB;
        var info = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["name"] = new Bencode.BytesNode(Encoding.UTF8.GetBytes(name)),
            ["piece length"] = new Bencode.IntNode(16384),
            ["pieces"] = new Bencode.BytesNode(pieces),
            ["length"] = new Bencode.IntNode(length),
        });
        return Bencode.Encode(info);
    }

    /// <summary>构造多文件 torrent 并附 CAS 扩展（用于测试 parse + CAS）。</summary>
    private static byte[] BuildMultiFileTorrentBytesWithCas(long pieceLength, long[] fileSizes)
    {
        const int totalPieces = 1;
        var pieces = new byte[totalPieces * 20];
        for (int i = 0; i < pieces.Length; i++) pieces[i] = 0xCD;

        var files = new List<Bencode.Node>();
        for (int i = 0; i < fileSizes.Length; i++)
        {
            files.Add(new Bencode.DictNode(new Dictionary<string, Bencode.Node>
            {
                ["length"] = new Bencode.IntNode(fileSizes[i]),
                ["path"] = new Bencode.ListNode(
                [
                    new Bencode.BytesNode(Encoding.UTF8.GetBytes("sub")),
                    new Bencode.BytesNode(Encoding.UTF8.GetBytes($"file{i}.bin")),
                ]),
            }));
        }

        var info = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["name"] = new Bencode.BytesNode(Encoding.UTF8.GetBytes("multi-root")),
            ["piece length"] = new Bencode.IntNode(pieceLength),
            ["pieces"] = new Bencode.BytesNode(pieces),
            ["files"] = new Bencode.ListNode(files),
        });
        var infoBytes = Bencode.Encode(info);

        var cas = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["cloud"] = new Bencode.BytesNode(Encoding.UTF8.GetBytes("189")),
            ["file_md5"] = new Bencode.BytesNode(Encoding.UTF8.GetBytes("DEADBEEFDEADBEEFDEADBEEFDEADBEEF")),
            ["slice_md5"] = new Bencode.BytesNode(Encoding.UTF8.GetBytes("CAFEBABEAB2345678901234567890ABC")),
            ["slice_size"] = new Bencode.IntNode(10 * 1024 * 1024),
        });

        var root = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["info"] = new Bencode.BytesNode(infoBytes),
            ["x-cas"] = cas,
        });
        return Bencode.Encode(root);
    }
}