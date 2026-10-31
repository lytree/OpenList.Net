using OpenList.Server.Hosting;

namespace OpenList.Tests;

/// <summary>
/// SPA 外壳注入（<c>customize_head</c> / <c>customize_body</c>）的单元测试。
/// <para>
/// 这两个设置项此前只有种子、没有任何消费方（见 docs/settings-gap-audit.md）；
/// 现在由 <c>Program.cs</c> 的 SPA fallback 注入到 <c>index.html</c>。
/// 这里锁定三条口径：插在正确位置、大小写不敏感、找不到标记时不丢内容。
/// </para>
/// </summary>
public class SpaHtmlInjectorTests
{
    private const string Shell = "<html><head><title>x</title></head><body><div id=app></div></body></html>";

    [Test]
    public async Task Apply_InsertsBeforeHeadAndBody()
    {
        var result = SpaHtmlInjector.Apply(Shell, "<script src=analytics.js></script>", "<div id=widget></div>");

        // head 片段插在 `</head>` 之前 —— 也就是标题之后、body 之前的最后位置，
        // 这样它不会挤在 charset/viewport 这些必须靠前的 meta 前面。
        await Assert.That(result).IsEqualTo(
            "<html><head><title>x</title><script src=analytics.js></script></head>"
            + "<body><div id=app></div><div id=widget></div></body></html>");
    }

    [Test]
    public async Task Apply_EmptySnippets_ReturnsInputUnchanged()
    {
        await Assert.That(SpaHtmlInjector.Apply(Shell, "", null)).IsEqualTo(Shell);
    }

    [Test]
    public async Task InjectBefore_IsCaseInsensitive()
    {
        var html = "<HTML><HEAD></HEAD><BODY></BODY></HTML>";
        var result = SpaHtmlInjector.InjectBefore(html, "</head>", "<meta name=x>");

        await Assert.That(result).IsEqualTo("<HTML><HEAD><meta name=x></HEAD><BODY></BODY></HTML>");
    }

    [Test]
    public async Task InjectBefore_MissingMarker_AppendsInsteadOfDropping()
    {
        // 自定义片段静默失效比排版偏移难排查，所以宁可追加到末尾也要让它生效
        var result = SpaHtmlInjector.InjectBefore("<div></div>", "</head>", "<script></script>");
        await Assert.That(result).IsEqualTo("<div></div><script></script>");
    }

    [Test]
    public async Task Apply_BodySnippetContainingHeadTag_DoesNotDisturbBodyInsertion()
    {
        // body 片段里带 "</head>" 字面量时，不能再被 head 的定位逻辑吃到
        var result = SpaHtmlInjector.Apply(Shell, "<meta name=a>", "<!-- </head> -->");

        await Assert.That(result).IsEqualTo(
            "<html><head><title>x</title><meta name=a></head>"
            + "<body><div id=app></div><!-- </head> --></body></html>");
    }
}
