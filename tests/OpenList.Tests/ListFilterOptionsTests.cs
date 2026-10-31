using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// 列表过滤（<c>hide_files</c> / <c>ignore_system_files</c>）的单元测试。
/// <para>
/// 这两个设置项此前只有种子、没有任何消费方（见 docs/settings-gap-audit.md）；
/// 现在过滤发生在 <c>fs/list</c> 的分页与索引发布之前，这里锁定「哪些名字会被隐藏」的口径。
/// </para>
/// </summary>
public class ListFilterOptionsTests
{
    // ---- ignore_system_files ----

    [Test]
    public async Task IgnoreSystemFiles_HidesDotfilesAndOsMetadata()
    {
        var options = ListFilterOptions.FromSettings(new Dictionary<string, string>
        {
            ["ignore_system_files"] = "true",
        });

        await Assert.That(options.ShouldHide(".DS_Store")).IsTrue();
        await Assert.That(options.ShouldHide(".gitignore")).IsTrue();
        await Assert.That(options.ShouldHide("Thumbs.db")).IsTrue();
        await Assert.That(options.ShouldHide("desktop.ini")).IsTrue();
        await Assert.That(options.ShouldHide("正常文件.txt")).IsFalse();
    }

    [Test]
    public async Task IgnoreSystemFiles_DisabledKeepsDotfiles()
    {
        var options = ListFilterOptions.FromSettings(new Dictionary<string, string>
        {
            ["ignore_system_files"] = "false",
        });

        await Assert.That(options.ShouldHide(".DS_Store")).IsFalse();
        await Assert.That(options.ShouldHide("Thumbs.db")).IsFalse();
    }

    // ---- hide_files ----

    [Test]
    public async Task HideFiles_MatchesLineSeparatedRegex()
    {
        var options = ListFilterOptions.FromSettings(new Dictionary<string, string>
        {
            ["hide_files"] = @"\.tmp$
^~\$",
        });

        await Assert.That(options.ShouldHide("a.tmp")).IsTrue();
        await Assert.That(options.ShouldHide("~$report.docx")).IsTrue();
        await Assert.That(options.ShouldHide("a.txt")).IsFalse();
    }

    [Test]
    public async Task HideFiles_InvalidRegexIsSkipped()
    {
        var warned = new List<string>();
        var options = ListFilterOptions.FromSettings(
            new Dictionary<string, string> { ["hide_files"] = "([unclosed\n\\.bak$" },
            warned.Add);

        // 非法正则只跳过并告警，合法的那条仍然生效 —— 一个写错的正则不该拖垮整页列表。
        await Assert.That(warned.Count).IsEqualTo(1);
        await Assert.That(options.ShouldHide("old.bak")).IsTrue();
        await Assert.That(options.ShouldHide("normal.txt")).IsFalse();
    }

    [Test]
    public async Task FromSettings_EmptySettingsHideNothing()
    {
        var options = ListFilterOptions.FromSettings(new Dictionary<string, string>());

        await Assert.That(options.IgnoreSystemFiles).IsFalse();
        await Assert.That(options.HideFilePatterns.Count).IsEqualTo(0);
        await Assert.That(options.ShouldHide("anything.bin")).IsFalse();
    }
}
