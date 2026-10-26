using System.IO.Compression;
using OpenList.Core.Drivers;
using OpenList.Drivers.Local;

namespace OpenList.Tests;

/// <summary>
/// 归档内条目流式读取（<see cref="IFileSystemArchiveReader.OpenArchiveEntryAsync"/>）的测试。
/// 覆盖：正常条目内容、可 Seek（Range 的前提）、缺失条目、ZipSlip 路径。
/// </summary>
public class ArchiveEntryStreamTests
{
    /// <summary>造一个含两层目录的 zip，返回 (临时根目录, 已初始化的 LocalDriver)。</summary>
    private static async Task<(string root, LocalDriver driver)> SetupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "openlist-arc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var hello = zip.CreateEntry("hello.txt");
            await using (var w = new StreamWriter(hello.Open())) await w.WriteAsync("hello from zip");
            var nested = zip.CreateEntry("sub/second.txt");
            await using (var w = new StreamWriter(nested.Open())) await w.WriteAsync("nested content");
        }

        var driver = new LocalDriver();
        await driver.InitAsync(new DriverConfig
        {
            MountPath = "/arc",
            Driver = "Local",
            Addition = $"{{\"root_folder_path\":\"{root.Replace("\\", "\\\\")}\"}}",
        });
        return (root, driver);
    }

    [Test]
    public async Task OpenArchiveEntryAsync_Returns_Seekable_Content()
    {
        var (root, driver) = await SetupAsync();
        try
        {
            var entries = await driver.ListArchiveEntriesAsync("/bundle.zip");
            await Assert.That(entries.Select(e => e.Name).ToList()).Contains("hello.txt");

            await using var stream = await driver.OpenArchiveEntryAsync("/bundle.zip", "sub/second.txt");
            await Assert.That(stream).IsNotNull();
            // 可 Seek 是 Range 请求的前提：预览器要能从中段读。
            await Assert.That(stream!.CanSeek).IsTrue();
            using var reader = new StreamReader(stream);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("nested content");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task OpenArchiveEntryAsync_Returns_Null_For_Missing_Entry()
    {
        var (root, driver) = await SetupAsync();
        try
        {
            var stream = await driver.OpenArchiveEntryAsync("/bundle.zip", "nope.txt");
            await Assert.That(stream).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task OpenArchiveEntryAsync_Rejects_Path_Traversal()
    {
        var (root, driver) = await SetupAsync();
        try
        {
            var stream = await driver.OpenArchiveEntryAsync("/bundle.zip", "../../etc/passwd");
            await Assert.That(stream).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
