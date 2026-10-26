using OpenList.Media.Services;

namespace OpenList.Tests;

/// <summary>
/// Kodi 兼容 NFO 的解析回归。真实世界的 .nfo 来自 Jellyfin / Kodi / TinyMediaManager
/// 等各家工具，字段拼写与结构差异大，这些用例锁定解析器的容错边界。
/// </summary>
public class MediaNfoReaderTests
{
    [Test]
    public async Task Parse_Movie_Nfo_Extracts_Core_Fields()
    {
        var nfo = MediaNfoReader.Parse("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes" ?>
            <movie>
              <title>Blade Runner 2049</title>
              <originaltitle>Blade Runner 2049</originaltitle>
              <year>2017</year>
              <plot>A young blade runner's discovery of a long-buried secret.</plot>
              <rating max="10" default="true">8.0</rating>
              <genre>Sci-Fi</genre>
              <genre>Drama</genre>
              <uniqueid type="imdb">tt1856101</uniqueid>
              <uniqueid type="tmdb" default="true">335984</uniqueid>
              <director>Denis Villeneuve</director>
              <actor>
                <name>Ryan Gosling</name>
                <role>K</role>
                <order>0</order>
              </actor>
              <actor>
                <name>Harrison Ford</name>
                <role>Rick Deckard</role>
                <order>1</order>
              </actor>
            </movie>
            """);

        await Assert.That(nfo).IsNotNull();
        await Assert.That(nfo!.Title).IsEqualTo("Blade Runner 2049");
        await Assert.That(nfo.Year).IsEqualTo(2017);
        await Assert.That(nfo.Rating).IsEqualTo(8.0);
        await Assert.That(nfo.TmdbId).IsEqualTo("335984");
        await Assert.That(nfo.Genres.Count).IsEqualTo(2);
        await Assert.That(nfo.Persons.Single(p => p.Role == "director").Name).IsEqualTo("Denis Villeneuve");
        await Assert.That(nfo.Persons.First(p => p.Role == "actor").Character).IsEqualTo("K");
        await Assert.That(nfo.Persons.Last(p => p.Role == "actor").Order).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_Tvshow_Nfo_Falls_Back_To_Premiered_Year()
    {
        var nfo = MediaNfoReader.Parse("""
            <tvshow>
              <title>Breaking Bad</title>
              <premiered>2008-01-20</premiered>
              <genre>Crime</genre>
              <credits>Vince Gilligan</credits>
            </tvshow>
            """);

        await Assert.That(nfo).IsNotNull();
        await Assert.That(nfo!.Year).IsEqualTo(2008);
        await Assert.That(nfo.Persons.Single(p => p.Role == "creator").Name).IsEqualTo("Vince Gilligan");
    }

    [Test]
    public async Task Parse_Rating_With_Five_Point_Max_Is_Normalised()
    {
        //豆瓣导出的 NFO 常见 5 分制评分，需要换算到 10 分制。
        var nfo = MediaNfoReader.Parse("""
            <movie>
              <title>Douban Movie</title>
              <rating max="5">4.0</rating>
            </movie>
            """);

        await Assert.That(nfo).IsNotNull();
        await Assert.That(nfo!.Rating).IsEqualTo(8.0);
    }

    [Test]
    public async Task Parse_Splits_Comma_Joined_Genres_And_Directors()
    {
        var nfo = MediaNfoReader.Parse("""
            <movie>
              <title>TinyMM Export</title>
              <genre>Sci-Fi, Drama</genre>
              <director>A | B</director>
            </movie>
            """);

        await Assert.That(nfo).IsNotNull();
        await Assert.That(nfo!.Genres.Count).IsEqualTo(2);
        await Assert.That(nfo.Persons.Count(p => p.Role == "director")).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_Tolerates_Multiple_Root_Elements()
    {
        // 部分工具在声明后并列写出多个根元素，标准 XDocument 会拒绝。
        var nfo = MediaNfoReader.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- comment -->
            <movie><title>Wrapped</title></movie>
            """);

        await Assert.That(nfo).IsNotNull();
        await Assert.That(nfo!.Title).IsEqualTo("Wrapped");
    }

    [Test]
    public async Task Parse_Returns_Null_For_Garbage_And_Empty_Content()
    {
        await Assert.That(MediaNfoReader.Parse("")).IsNull();
        await Assert.That(MediaNfoReader.Parse("   ")).IsNull();
        await Assert.That(MediaNfoReader.Parse("this is not xml at all <<<")).IsNull();
        await Assert.That(MediaNfoReader.Parse("<movie></movie>")).IsNull();
        await Assert.That(MediaNfoReader.Parse("<other><title>x</title></other>")).IsNull();
    }
}

/// <summary>sidecar（NFO/海报/背景图）挑选规则回归。</summary>
public class MediaSidecarSelectionTests
{
    [Test]
    public async Task SelectNfoPath_Prefers_Item_Name_Then_Convention_Then_Unique()
    {
        var nfos = new[] { "/lib/Movie A/fanart.nfo", "/lib/Movie A/movie.nfo", "/lib/Movie A/Movie A.nfo" };
        await Assert.That(MediaNaming.SelectNfoPath("/lib/Movie A", "/lib", nfos))
            .IsEqualTo("/lib/Movie A/Movie A.nfo");

        await Assert.That(MediaNaming.SelectNfoPath("/lib/Movie A", "/lib", ["/lib/Movie A/movie.nfo"]))
            .IsEqualTo("/lib/Movie A/movie.nfo");

        await Assert.That(MediaNaming.SelectNfoPath("/lib/Movie A", "/lib", ["/lib/Movie A/whatever.nfo"]))
            .IsEqualTo("/lib/Movie A/whatever.nfo");
    }

    [Test]
    public async Task SelectNfoPath_Gives_Up_On_Ambiguous_Unnamed_Files()
    {
        var nfos = new[] { "/lib/Movie A/a.nfo", "/lib/Movie A/b.nfo" };
        await Assert.That(MediaNaming.SelectNfoPath("/lib/Movie A", "/lib", nfos)).IsNull();
    }

    [Test]
    public async Task SelectNfoPath_Ignores_Files_Outside_Sidecar_Directory()
    {
        var nfos = new[] { "/lib/Other/movie.nfo" };
        // 条目目录里没有 NFO 时不应捡到别的目录的。
        await Assert.That(MediaNaming.SelectNfoPath("/lib/Movie A", "/lib", nfos)).IsNull();
    }

    [Test]
    public async Task SelectNfoPath_Matches_Loose_File_Item_By_Name()
    {
        // 库根散文件条目：Movie.mkv 的 NFO 是同目录的 Movie.nfo。
        var nfos = new[] { "/lib/loose/Movie.nfo" };
        await Assert.That(MediaNaming.SelectNfoPath("/lib/loose/Movie.mkv", "/lib/loose", nfos))
            .IsEqualTo("/lib/loose/Movie.nfo");
    }

    [Test]
    public async Task SelectImagePaths_Handles_Exact_Variant_And_Deterministic_Fallback()
    {
        var images = new[]
        {
            "/lib/Movie A/fanart.jpg", "/lib/Movie A/Movie A-poster.jpg",
            "/lib/Movie A/cover.jpg", "/lib/Movie A/backdrop.png",
        };
        var (poster, backdrop) = MediaNaming.SelectImagePaths("/lib/Movie A", "/lib", images);
        // 精确基名不存在，但 <条目名>-poster.jpg 变体命中。
        await Assert.That(poster).IsEqualTo("/lib/Movie A/Movie A-poster.jpg");
        // fanart 与 backdrop 同为背景图候选，按字典序取确定的一个。
        await Assert.That(backdrop).IsEqualTo("/lib/Movie A/backdrop.png");
    }

    [Test]
    public async Task SelectImagePaths_Prefers_Exact_BaseName()
    {
        var images = new[] { "/lib/Movie A/folder.jpg", "/lib/Movie A/poster.jpg" };
        var (poster, _) = MediaNaming.SelectImagePaths("/lib/Movie A", "/lib", images);
        await Assert.That(poster).IsEqualTo("/lib/Movie A/poster.jpg");
    }

    [Test]
    public async Task ClassifyImageFile_Distinguishes_Posters_Backdrops_And_Others()
    {
        await Assert.That(MediaNaming.ClassifyImageFile("poster.jpg")).IsEqualTo("poster");
        await Assert.That(MediaNaming.ClassifyImageFile("FOLDER.PNG")).IsEqualTo("poster");
        await Assert.That(MediaNaming.ClassifyImageFile("fanart.jpg")).IsEqualTo("backdrop");
        await Assert.That(MediaNaming.ClassifyImageFile("backdrop.webp")).IsEqualTo("backdrop");
        await Assert.That(MediaNaming.ClassifyImageFile("screenshot.jpg")).IsNull();
        await Assert.That(MediaNaming.ClassifyImageFile("poster.gif")).IsNull();
    }
}
