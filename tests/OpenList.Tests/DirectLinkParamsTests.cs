using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// 直链参数转发（<c>forward_direct_link_params</c> / <c>ignore_direct_link_params</c>）的规则测试。
/// <para>
/// 这一层不依赖 HTTP：<see cref="DirectLinkParams.Build"/> 是纯函数，端点只负责把
/// <c>HttpRequest.Query</c> 摊平成键值对。规则细节（覆盖而非追加、sign 恒丢弃、
/// 空值丢弃、fragment 保留）都在这里钉住。
/// </para>
/// </summary>
public class DirectLinkParamsTests
{
    /// <summary>构造请求参数序列（值可能为 null，模拟 <c>?flag</c> 这种无值参数）。</summary>
    private static List<KeyValuePair<string, string?>> Params(params (string Key, string? Value)[] items)
        => items.Select(i => new KeyValuePair<string, string?>(i.Key, i.Value)).ToList();

    [Test]
    public async Task Build_Leaves_Url_Untouched_When_Forward_Disabled()
    {
        var url = "https://cdn.example.com/a.zip";
        var result = DirectLinkParams.Build(url, Params(("download", "1")), forward: false, ignoreList: null);
        await Assert.That(result).IsEqualTo(url);
    }

    [Test]
    public async Task Build_Appends_Request_Params_To_Bare_Url()
    {
        var result = DirectLinkParams.Build(
            "https://cdn.example.com/a.zip", Params(("download", "1"), ("v", "2")), forward: true, ignoreList: null);
        await Assert.That(result).IsEqualTo("https://cdn.example.com/a.zip?download=1&v=2");
    }

    [Test]
    public async Task Build_Keeps_Existing_Query_And_Overrides_On_Name_Conflict()
    {
        // 覆盖而非追加：重复参数会让上游按第一个值处理，表现为「设置了却没生效」。
        var result = DirectLinkParams.Build(
            "https://cdn.example.com/a.zip?token=old&keep=1",
            Params(("token", "new")),
            forward: true,
            ignoreList: null);
        await Assert.That(result).IsEqualTo("https://cdn.example.com/a.zip?token=new&keep=1");
    }

    [Test]
    public async Task Build_Never_Forwards_Sign()
    {
        var result = DirectLinkParams.Build(
            "https://cdn.example.com/a.zip", Params(("sign", "abc123")), forward: true, ignoreList: null);
        await Assert.That(result).IsEqualTo("https://cdn.example.com/a.zip");
    }

    [Test]
    public async Task Build_Drops_Ignored_Names_Case_Insensitively()
    {
        var result = DirectLinkParams.Build(
            "https://cdn.example.com/a.zip",
            Params(("Preview", "1"), ("download", "1")),
            forward: true,
            ignoreList: "preview, nope\nother");
        await Assert.That(result).IsEqualTo("https://cdn.example.com/a.zip?download=1");
    }

    [Test]
    public async Task Build_Drops_Empty_Values()
    {
        // 空值会被上游当成「显式清空该参数」，比不转发更糟。
        var result = DirectLinkParams.Build(
            "https://cdn.example.com/a.zip", Params(("a", ""), ("b", null)), forward: true, ignoreList: null);
        await Assert.That(result).IsEqualTo("https://cdn.example.com/a.zip");
    }

    [Test]
    public async Task Build_Preserves_Fragment_And_Encodes_Values()
    {
        var result = DirectLinkParams.Build(
            "https://cdn.example.com/a.zip#frag",
            Params(("name", "中 文&x")),
            forward: true,
            ignoreList: null);
        // 值里的空格与 & 必须百分号编码，否则上游会把它们当成新参数。
        await Assert.That(result).IsEqualTo("https://cdn.example.com/a.zip?name=%E4%B8%AD%20%E6%96%87%26x#frag");
    }

    [Test]
    public async Task ParseIgnored_Always_Contains_Sign_And_Splits_On_Mixed_Separators()
    {
        var set = DirectLinkParams.ParseIgnored("a，b;c\nd  e");
        foreach (var name in new[] { "sign", "a", "b", "c", "d", "e" })
            await Assert.That(set.Contains(name)).IsTrue();
        await Assert.That(set.Count).IsEqualTo(6);
    }
}
