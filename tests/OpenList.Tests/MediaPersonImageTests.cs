using OpenList.Media.Services;

namespace OpenList.Tests;

/// <summary>
/// 人物头像在「NFO 文本」与「VFS 路径」之间的换算。
/// 两个方向都要钉住：读错了头像显示不出来，写错了会把 NFO 绑死在某台服务器的挂载布局上。
/// </summary>
public class MediaPersonImageTests
{
    // ------------------------------------------------------------------
    // NFO thumb → VFS 路径
    // ------------------------------------------------------------------

    [Test]
    public async Task ToVfsPath_Relative_Name_Joins_Item_Directory()
    {
        await Assert.That(MediaPersonImage.ToVfsPath("harrison.jpg", "/e2e/Blade Runner (1982)"))
            .IsEqualTo("/e2e/Blade Runner (1982)/harrison.jpg");
    }

    [Test]
    public async Task ToVfsPath_Handles_Nested_And_Sloppy_Separators()
    {
        await Assert.That(MediaPersonImage.ToVfsPath(".actors/john.jpg", "/e2e/Movie"))
            .IsEqualTo("/e2e/Movie/.actors/john.jpg");
        // Windows 风格分隔符与重复斜杠都要能收
        await Assert.That(MediaPersonImage.ToVfsPath(@"img\\a.jpg", "/e2e/Movie/"))
            .IsEqualTo("/e2e/Movie/img/a.jpg");
        await Assert.That(MediaPersonImage.ToVfsPath("//e2e//poster.jpg", "/e2e/Movie"))
            .IsEqualTo("/e2e/poster.jpg");
    }

    [Test]
    public async Task ToVfsPath_Keeps_Absolute_Url_Untouched()
    {
        const string url = "https://image.tmdb.org/t/p/w185/abc.jpg";
        await Assert.That(MediaPersonImage.ToVfsPath(url, "/e2e/Movie")).IsEqualTo(url);
        await Assert.That(MediaPersonImage.ToVfsPath("http://cdn.example.com/a.jpg", "/e2e/Movie"))
            .IsEqualTo("http://cdn.example.com/a.jpg");
    }

    /// <summary>
    /// NFO 是用户可写内容：`..` 必须挡掉，否则一条 `<thumb>../../../etc/passwd</thumb>`
    /// 就能让图片端点去读条目目录之外的文件（管理员身份下更容易踩到）。
    /// </summary>
    [Test]
    public async Task ToVfsPath_Rejects_Parent_Traversal()
    {
        await Assert.That(MediaPersonImage.ToVfsPath("../secret.jpg", "/e2e/Movie")).IsNull();
        await Assert.That(MediaPersonImage.ToVfsPath("a/../../b.jpg", "/e2e/Movie")).IsNull();
        await Assert.That(MediaPersonImage.ToVfsPath(@"..\\b.jpg", "/e2e/Movie")).IsNull();
    }

    [Test]
    public async Task ToVfsPath_Empty_Input_Returns_Null()
    {
        await Assert.That(MediaPersonImage.ToVfsPath(null, "/e2e/Movie")).IsNull();
        await Assert.That(MediaPersonImage.ToVfsPath("", "/e2e/Movie")).IsNull();
        await Assert.That(MediaPersonImage.ToVfsPath("   ", "/e2e/Movie")).IsNull();
    }

    // ------------------------------------------------------------------
    // VFS 路径 → NFO thumb
    // ------------------------------------------------------------------

    [Test]
    public async Task ToNfoValue_Writes_Only_File_Name_For_Local_Image()
    {
        // 写绝对路径会让 NFO 换台机器就失效（挂载点可能不同），只保留文件名
        await Assert.That(MediaPersonImage.ToNfoValue("/e2e/Movie/.actors/john.jpg")).IsEqualTo("john.jpg");
        await Assert.That(MediaPersonImage.ToNfoValue("D:/media/a.png")).IsEqualTo("a.png");
    }

    [Test]
    public async Task ToNfoValue_Keeps_Remote_Url()
    {
        const string url = "https://image.tmdb.org/t/p/w185/abc.jpg";
        await Assert.That(MediaPersonImage.ToNfoValue(url)).IsEqualTo(url);
    }

    [Test]
    public async Task ToNfoValue_Empty_Input_Returns_Null()
    {
        await Assert.That(MediaPersonImage.ToNfoValue(null)).IsNull();
        await Assert.That(MediaPersonImage.ToNfoValue("  ")).IsNull();
    }

    /// <summary>读→写→读 应当收敛：落库的路径再写回 NFO 后，重新扫描还能解析回同一路径。</summary>
    [Test]
    public async Task Round_Trip_Converges()
    {
        const string dir = "/e2e/Movie";
        var stored = MediaPersonImage.ToVfsPath("harrison.jpg", dir);
        await Assert.That(stored).IsEqualTo("/e2e/Movie/harrison.jpg");

        var back = MediaPersonImage.ToNfoValue(stored);
        await Assert.That(back).IsEqualTo("harrison.jpg");
        await Assert.That(MediaPersonImage.ToVfsPath(back, dir)).IsEqualTo(stored);
    }
}
