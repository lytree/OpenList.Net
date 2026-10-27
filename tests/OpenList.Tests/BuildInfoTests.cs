using OpenList.Server.Hosting;

namespace OpenList.Tests;

/// <summary>
/// 构建信息里的版本解析（管理台「关于」页的数据源）。
///
/// 只钉一件事：<c>AssemblyInformationalVersion</c> 里 <c>+</c> 左边是 semver、右边是提交 SHA。
/// 这条规则错位的后果很隐蔽 —— 页面上会显示一个「看着像版本号」的 sha 前缀，
/// 或者把预发布后缀 <c>-e2e</c> 当成 commit 的一部分，用户不会察觉。
/// </summary>
public class BuildInfoTests
{
    [Test]
    public async Task Parse_Splits_Semver_And_Commit()
    {
        var (version, commit) = BuildInfo.Parse("4.2.6+07d789e2b9a81acd8d369a1aaad52afbf713168e");

        await Assert.That(version).IsEqualTo("4.2.6");
        await Assert.That(commit).IsEqualTo("07d789e2b9a81acd8d369a1aaad52afbf713168e");
    }

    [Test]
    public async Task Parse_Keeps_Prerelease_Suffix_In_Version()
    {
        // Release 构建会注入 `--semver 0.0.1-e2e`；预发布后缀属于版本，不属于提交
        var (version, commit) = BuildInfo.Parse("0.0.1-e2e+ae21a4ffc993a0c3d6228ba947ef5d8e36178319");

        await Assert.That(version).IsEqualTo("0.0.1-e2e");
        await Assert.That(commit).IsEqualTo("ae21a4ffc993a0c3d6228ba947ef5d8e36178319");
    }

    [Test]
    public async Task Parse_Without_Metadata_Has_Empty_Commit()
    {
        var (version, commit) = BuildInfo.Parse("1.0.0");

        await Assert.That(version).IsEqualTo("1.0.0");
        await Assert.That(commit).IsEqualTo("");
    }

    [Test]
    public async Task Parse_Blank_Falls_Back_To_Zero()
    {
        // 注入链路断掉时 InformationalVersion 可能是空串；不能把 null 直接显示到页面上
        foreach (var raw in new[] { null, "", "   " })
        {
            var (version, commit) = BuildInfo.Parse(raw);
            await Assert.That(version).IsEqualTo("0.0.0");
            await Assert.That(commit).IsEqualTo("");
        }
    }

    [Test]
    public async Task Parse_Trailing_Plus_Keeps_Version_And_Empty_Commit()
    {
        // 有 `+` 但没有元数据：版本照留，提交留空，而不是把版本也吞掉
        var (version, commit) = BuildInfo.Parse("2.3.4+");

        await Assert.That(version).IsEqualTo("2.3.4");
        await Assert.That(commit).IsEqualTo("");
    }

    [Test]
    public async Task Tag_And_Version_Agree()
    {
        // 页面上会同时出现 `v1.0.0`（tag 样式）与程序集版本，两者必须同源
        await Assert.That(BuildInfo.Tag).IsEqualTo("v" + BuildInfo.Version);
        await Assert.That(BuildInfo.Version.Length > 0).IsTrue();
        await Assert.That(BuildInfo.Configuration is "Debug" or "Release").IsTrue();
    }
}
