using System.Text;
using OpenList.Core.Drivers;
using OpenList.Drivers.Local;

namespace OpenList.Tests;

/// <summary>
/// <c>non_efs_zip_encoding</c>（非 UTF-8 标志位 zip 的条目名解码）的行为测试。
/// <para>
/// 场景来源：Windows 自带「发送到 → 压缩文件夹」与一批老工具写出的 zip，条目名用本地代码页
/// （简体中文是 GBK）且 <b>不设</b>通用标志位 bit 11，SharpCompress 默认按 UTF-8 解，中文名会成乱码。
/// 这里用 <see cref="LegacyZipBuilder"/> 手工拼一个这样的包，再验证
/// 「按设置解码后名字正确、且能按名字取到内容」。
/// </para>
/// </summary>
public class LegacyZipEncodingTests
{
    /// <summary>在临时目录里放一个 GBK 名字的 zip，返回 (root, driver, 中文条目名)。</summary>
    private static async Task<(string root, LocalDriver driver, string chineseName)> SetupAsync()
    {
        const string chineseName = "中文目录/说明.txt";
        var zip = LegacyZipBuilder.BuildWithGbkName(chineseName, "legacy zip content");

        var root = Path.Combine(Path.GetTempPath(), "openlist-zipenc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "legacy.zip"), zip);

        var driver = new LocalDriver();
        await driver.InitAsync(new DriverConfig
        {
            MountPath = "/enc",
            Driver = "Local",
            Addition = $"{{\"root_folder_path\":\"{root.Replace("\\", "\\\\")}\"}}",
        });
        return (root, driver, chineseName);
    }

    [Test]
    public async Task ListArchiveEntries_Decodes_Gbk_Names_When_Encoding_Configured()
    {
        var (root, driver, chineseName) = await SetupAsync();
        try
        {
            var entries = await driver.ListArchiveEntriesAsync("/legacy.zip", entryEncoding: "gbk");
            await Assert.That(entries.Select(e => e.Name).ToList()).Contains(chineseName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ListArchiveEntries_Keeps_Mojibake_Without_Encoding()
    {
        // 反向断言：不配置时确实是乱码 —— 否则这条设置就是「本来就好，加了没用」。
        var (root, driver, chineseName) = await SetupAsync();
        try
        {
            var entries = await driver.ListArchiveEntriesAsync("/legacy.zip");
            await Assert.That(entries.Select(e => e.Name).ToList()).DoesNotContain(chineseName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task OpenArchiveEntry_Finds_Entry_By_Decoded_Name()
    {
        // 预览链路按「列表里看到的名字」回头取内容，解码必须两处一致。
        var (root, driver, chineseName) = await SetupAsync();
        try
        {
            var entries = await driver.ListArchiveEntriesAsync("/legacy.zip", entryEncoding: "gbk");
            await Assert.That(entries.Select(e => e.Name).ToList()).Contains(chineseName);

            await using var stream = await driver.OpenArchiveEntryAsync(
                "/legacy.zip", chineseName, entryEncoding: "gbk");
            await Assert.That(stream).IsNotNull();
            using var reader = new StreamReader(stream!);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("legacy zip content");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Unknown_Encoding_Name_Falls_Back_Instead_Of_Throwing()
    {
        // 管理员手滑写错编码名，不能让「打开压缩包」直接 500。
        var (root, driver, _) = await SetupAsync();
        try
        {
            var entries = await driver.ListArchiveEntriesAsync("/legacy.zip", entryEncoding: "no-such-encoding");
            await Assert.That(entries.Count).IsGreaterThan(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
