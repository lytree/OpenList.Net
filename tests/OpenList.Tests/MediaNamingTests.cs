using OpenList.Media.Domain;
using OpenList.Media.Services;

namespace OpenList.Tests;

/// <summary>
/// 扫描命名规则回归：这些规则决定入库后的标题/年份/条目边界，
/// 一旦回退（例如把 "Blade Runner 2049" 切成 "Blade Runner"）用户侧看得到，
/// 但以前只藏在扫描流程里、没有测试覆盖。
/// </summary>
public class MediaNamingTests
{
    [Test]
    public async Task ParseTitle_Prefers_Parenthesised_Year_Over_Number_In_Title()
    {
        var (title, year) = MediaNaming.ParseTitle("/lib/Blade Runner 2049 (2017)", "/lib");
        await Assert.That(title).IsEqualTo("Blade Runner 2049");
        await Assert.That(year).IsEqualTo(2017);
    }

    [Test]
    public async Task ParseTitle_Falls_Back_To_Bare_Year_When_No_Brackets()
    {
        var (title, year) = MediaNaming.ParseTitle("/lib/Interstellar.2014.1080p", "/lib");
        await Assert.That(title).IsEqualTo("Interstellar");
        await Assert.That(year).IsEqualTo(2014);
    }

    [Test]
    public async Task ParseTitle_Leaves_Title_Without_Year_Intact()
    {
        var (title, year) = MediaNaming.ParseTitle("/lib/Some Movie", "/lib");
        await Assert.That(title).IsEqualTo("Some Movie");
        await Assert.That(year).IsNull();
    }

    [Test]
    public async Task ParseTitle_Strips_Video_Extension_When_Item_Key_Is_A_File()
    {
        var (title, year) = MediaNaming.ParseTitle("/lib/loose/Inception (2010).mkv", "/lib/loose");
        await Assert.That(title).IsEqualTo("Inception");
        await Assert.That(year).IsEqualTo(2010);
    }

    [Test]
    public async Task ParseTitle_Normalises_Dots_Underscores_And_Brackets()
    {
        var (title, _) = MediaNaming.ParseTitle("/lib/[Group] The_Matrix.1999.[x264]", "/lib");
        await Assert.That(title).IsEqualTo("The Matrix");
    }

    [Test]
    public async Task ParseTitle_Keeps_Title_When_Only_Brackets_Remain()
    {
        // 不能因为清洗过度产出空串：空标题会直接显示成空白卡片。
        var (title, _) = MediaNaming.ParseTitle("/lib/(2019)", "/lib");
        await Assert.That(string.IsNullOrWhiteSpace(title)).IsFalse();
    }

    [Test]
    public async Task ParseTitle_Uses_Show_Name_When_Item_Directory_Is_A_Season_Folder()
    {
        var (title, _) = MediaNaming.ParseTitle("/lib/Breaking Bad/Season 1", "/lib");
        await Assert.That(title).IsEqualTo("Breaking Bad");
    }

    [Test]
    public async Task ParseTitle_Keeps_Season_Name_When_Season_Sits_At_Library_Root()
    {
        // 再往上就是库根了，没有更好的名字可用，不能退化成空标题。
        var (title, _) = MediaNaming.ParseTitle("/lib/Season 1", "/lib");
        await Assert.That(title).IsEqualTo("Season 1");
    }

    [Test]
    public async Task ResolveItemDirectory_Groups_Files_Under_Their_Folder()    {
        var dir = MediaNaming.ResolveItemDirectory("/lib/Movie A/Movie A.mkv", "/lib");
        await Assert.That(dir).IsEqualTo("/lib/Movie A");
    }

    [Test]
    public async Task ResolveItemDirectory_Uses_File_Itself_When_Placed_At_Library_Root()
    {
        // 库根的散文件如果共享父目录，整个库会被并成一个条目。
        var dir = MediaNaming.ResolveItemDirectory("/lib/loose.mkv", "/lib");
        await Assert.That(dir).IsEqualTo("/lib/loose.mkv");
    }

    [Test]
    public async Task ResolveItemType_Detects_Seasons_Only_For_Mixed_Libraries()
    {
        await Assert.That(MediaNaming.ResolveItemType(MediaLibraryType.Mixed, "/lib", "/lib/Show/Season 1"))
            .IsEqualTo(MediaLibraryType.TvShow);
        await Assert.That(MediaNaming.ResolveItemType(MediaLibraryType.Mixed, "/lib", "/lib/Show/第一季"))
            .IsEqualTo(MediaLibraryType.TvShow);
        await Assert.That(MediaNaming.ResolveItemType(MediaLibraryType.Mixed, "/lib", "/lib/Movie A"))
            .IsEqualTo(MediaLibraryType.Movie);
        // 显式声明的库类型不被目录名覆盖。
        await Assert.That(MediaNaming.ResolveItemType(MediaLibraryType.Movie, "/lib", "/lib/Show/Season 1"))
            .IsEqualTo(MediaLibraryType.Movie);
    }

    [Test]
    public async Task IsVideoFile_Matches_By_Extension_Case_Insensitively()
    {
        await Assert.That(MediaNaming.IsVideoFile("A.MKV")).IsTrue();
        await Assert.That(MediaNaming.IsVideoFile("A.mp4")).IsTrue();
        await Assert.That(MediaNaming.IsVideoFile("A.nfo")).IsFalse();
        await Assert.That(MediaNaming.IsVideoFile("A")).IsFalse();
    }
}

/// <summary>题材列的读写口径（一列 JSON 文本 ↔ 内存集合）。</summary>
public class MediaGenresTests
{
    [Test]
    public async Task SetGenres_Serialises_To_Json_And_Exposes_Typed_View()
    {
        var item = new MediaItem();
        item.SetGenres(["Sci-Fi", "Drama"]);

        await Assert.That(item.GenresJson).IsNotNull();
        await Assert.That(item.Genres.Count).IsEqualTo(2);
        await Assert.That(item.HasGenre("sci-fi")).IsTrue();
        await Assert.That(item.HasGenre("Comedy")).IsFalse();
    }

    [Test]
    public async Task SetGenres_With_Empty_Collection_Clears_Column()
    {
        var item = new MediaItem { GenresJson = """["Drama"]""" };
        item.SetGenres([]);
        await Assert.That(item.GenresJson).IsNull();
    }

    [Test]
    public async Task Parse_Tolerates_Legacy_Garbage()
    {
        // 老库允许任意文本：坏数据不能让列表接口 500。
        await Assert.That(MediaGenres.Parse("not json").Count).IsEqualTo(0);
        await Assert.That(MediaGenres.Parse("").Count).IsEqualTo(0);
        await Assert.That(MediaGenres.Parse(null).Count).IsEqualTo(0);
        await Assert.That(MediaGenres.Parse("""["A","A"," B "]""").Count).IsEqualTo(2);
    }
}
