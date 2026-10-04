using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Torrent;
using OpenList.Infrastructure.Services;
using Zio;
using Zio.FileSystems;

namespace OpenList.Tests;

/// <summary>
/// <see cref="TorrentService"/> 的单元测试：解析、秒传降级。
/// </summary>
public class TorrentServiceTests
{
    private static (TorrentService svc, MemoryFileSystem mem) Setup()
    {
        var mount = new ZioMountService();
        var mem = new MemoryFileSystem();
        // 挂载到 /dest 以便 RapidUploadAsync 的 FindDriverForPath("/dest") 能找到驱动。
        mount.Root.Mount("/dest", mem);
        return (new TorrentService(mount), mem);
    }

    [Test]
    public async Task ParseAsync_Returns_Details_ForSingleFileTorrent()
    {
        var (svc, _) = Setup();
        var bytes = BuildTorrentBase64("hello.txt", 11);
        var r = await svc.ParseAsync(bytes);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Name).IsEqualTo("hello.txt");
        await Assert.That(r.Data!.TotalSize).IsEqualTo(11);
        await Assert.That(r.Data!.PieceCount).IsEqualTo(1);
        await Assert.That(r.Data!.Files.Count).IsEqualTo(1);
        await Assert.That(r.Data!.Files[0].Path).IsEqualTo("hello.txt");
        await Assert.That(r.Data!.Files[0].Size).IsEqualTo(11);
        await Assert.That(r.Data!.HasCas).IsFalse();
        await Assert.That(r.Data!.Cas).IsNull();
    }

    [Test]
    public async Task ParseAsync_ReturnsCasFields_WhenCasPresent()
    {
        var (svc, _) = Setup();
        var bytes = BuildMultiFileTorrentBase64WithCas();
        var r = await svc.ParseAsync(bytes);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.HasCas).IsTrue();
        await Assert.That(r.Data!.Cas).IsNotNull();
        await Assert.That(r.Data!.Cas!.Cloud).IsEqualTo("189");
        await Assert.That(r.Data!.Cas!.FileMd5).IsEqualTo("DEADBEEFDEADBEEFDEADBEEFDEADBEEF");
    }

    [Test]
    public async Task ParseAsync_Rejects_InvalidBase64()
    {
        var (svc, _) = Setup();
        var r = await svc.ParseAsync("@@not-base64@@");
        await Assert.That(r.Code).IsEqualTo(400);
    }

    [Test]
    public async Task ParseAsync_Rejects_Empty()
    {
        var (svc, _) = Setup();
        var r = await svc.ParseAsync(string.Empty);
        await Assert.That(r.Code).IsEqualTo(400);
    }

    [Test]
    public async Task RapidUploadAsync_Degrades_To_NotImplemented_When_DriverUnsupported()
    {
        var (svc, _) = Setup();
        var bytes = BuildMultiFileTorrentBase64WithCas();
        var r = await svc.RapidUploadAsync(bytes, "/dest", new User { Id = 1, Role = UserRole.Admin });
        await Assert.That(r.Code).IsEqualTo(501);
        await Assert.That(r.Message).Contains("天翼云PC驱动");
    }

    [Test]
    public async Task RapidUploadAsync_Rejects_MissingCas()
    {
        var (svc, _) = Setup();
        var bytes = BuildTorrentBase64("hello.txt", 11);
        var r = await svc.RapidUploadAsync(bytes, "/dest", new User { Id = 1, Role = UserRole.Admin });
        await Assert.That(r.Code).IsEqualTo(400);
        await Assert.That(r.Message).Contains("CAS");
    }

    [Test]
    public async Task RapidUploadAsync_Rejects_NonAdmin_NonWriter()
    {
        var (svc, _) = Setup();
        var bytes = BuildMultiFileTorrentBase64WithCas();
        var user = new User { Id = 2, Role = UserRole.General, Permission = 0 };
        var r = await svc.RapidUploadAsync(bytes, "/dest", user);
        await Assert.That(r.Code).IsEqualTo(403);
    }

    // --- 测试夹具：构造 bencoded torrent 并 base64 编码 ---

    private static string BuildTorrentBase64(string name, long length)
    {
        var pieces = new byte[20];
        for (int i = 0; i < pieces.Length; i++) pieces[i] = 0xAB;
        var info = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["name"] = new Bencode.BytesNode(System.Text.Encoding.UTF8.GetBytes(name)),
            ["piece length"] = new Bencode.IntNode(16384),
            ["pieces"] = new Bencode.BytesNode(pieces),
            ["length"] = new Bencode.IntNode(length),
        });
        var infoBytes = Bencode.Encode(info);
        var root = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["info"] = new Bencode.BytesNode(infoBytes),
        });
        return Convert.ToBase64String(Bencode.Encode(root));
    }

    private static string BuildMultiFileTorrentBase64WithCas()
    {
        var pieces = new byte[20];
        for (int i = 0; i < pieces.Length; i++) pieces[i] = 0xCD;
        var files = new List<Bencode.Node>
        {
            new Bencode.DictNode(new Dictionary<string, Bencode.Node>
            {
                ["length"] = new Bencode.IntNode(1024),
                ["path"] = new Bencode.ListNode(
                [
                    new Bencode.BytesNode(System.Text.Encoding.UTF8.GetBytes("file1.bin")),
                ]),
            }),
        };
        var info = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["name"] = new Bencode.BytesNode(System.Text.Encoding.UTF8.GetBytes("multi")),
            ["piece length"] = new Bencode.IntNode(16384),
            ["pieces"] = new Bencode.BytesNode(pieces),
            ["files"] = new Bencode.ListNode(files),
        });
        var infoBytes = Bencode.Encode(info);
        var cas = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["cloud"] = new Bencode.BytesNode(System.Text.Encoding.UTF8.GetBytes("189")),
            ["file_md5"] = new Bencode.BytesNode(System.Text.Encoding.UTF8.GetBytes("DEADBEEFDEADBEEFDEADBEEFDEADBEEF")),
            ["slice_md5"] = new Bencode.BytesNode(System.Text.Encoding.UTF8.GetBytes("CAFEBABEAB2345678901234567890ABC")),
            ["slice_size"] = new Bencode.IntNode(10 * 1024 * 1024),
        });
        var root = new Bencode.DictNode(new Dictionary<string, Bencode.Node>
        {
            ["info"] = new Bencode.BytesNode(infoBytes),
            ["x-cas"] = cas,
        });
        return Convert.ToBase64String(Bencode.Encode(root));
    }
}