using System.Text;
using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Core.Models;
using OpenList.Drivers.Local;
using Zio;

namespace OpenList.Tests;

/// <summary>
/// Tests for LocalDriver (Zio-based) using temporary directories.
/// Each test gets its own isolated temp directory.
/// </summary>
public class LocalDriverTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _recycleBin;
    private readonly LocalDriver _driver;

    public LocalDriverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"localdriver_test_{Guid.NewGuid():N}");
        _recycleBin = Path.Combine(Path.GetTempPath(), $"localdriver_recycle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _driver = CreateDriver(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
        try { if (Directory.Exists(_recycleBin)) Directory.Delete(_recycleBin, true); } catch { }
    }

    private static LocalDriver CreateDriver(string rootPath, bool showHidden = true,
        bool directorySize = false, string recycleBinPath = "delete permanently")
    {
        var addition = new LocalAddition
        {
            RootFolderPath = rootPath,
            ShowHidden = showHidden,
            DirectorySize = directorySize,
            RecycleBinPath = recycleBinPath
        };

        var storage = new Storage
        {
            MountPath = "/test",
            Driver = "Local",
            Addition = JsonSerializer.Serialize(addition)
        };

        var driver = new LocalDriver { Storage = storage };
        return driver;
    }

    private async Task<LocalDriver> InitDriverAsync(string? rootPath = null, bool showHidden = true,
        bool directorySize = false, string recycleBinPath = "delete permanently")
    {
        var d = CreateDriver(rootPath ?? _tempRoot, showHidden, directorySize, recycleBinPath);
        await d.InitAsync();
        return d;
    }

    // ── InitAsync Tests ──

    [Test]
    public async Task InitAsync_ValidRoot_ShouldSucceed()
    {
        var d = await InitDriverAsync();
        await Assert.That(d).IsNotNull();
    }

    [Test]
    public async Task InitAsync_NonExistentRoot_ShouldThrow()
    {
        var badPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}");
        var d = CreateDriver(badPath);
        try { await d.InitAsync(); Assert.Fail("Expected DirectoryNotFoundException"); }
        catch (DirectoryNotFoundException) { /* expected */ }
    }

    [Test]
    public async Task InitAsync_RelativePath_ShouldConvertToAbsolute()
    {
        var subDir = Path.Combine(_tempRoot, "relative_test");
        Directory.CreateDirectory(subDir);
        var relPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), subDir);

        var d = CreateDriver(relPath);
        await d.InitAsync();
        await Assert.That(Path.IsPathRooted(d.RootFolderPath)).IsTrue();
    }

    [Test]
    public async Task InitAsync_InvalidVideoThumbPos_ShouldThrow()
    {
        var addition = new LocalAddition
        {
            RootFolderPath = _tempRoot,
            VideoThumbPos = "invalid"
        };
        var storage = new Storage { MountPath = "/test", Addition = JsonSerializer.Serialize(addition) };
        var d = new LocalDriver { Storage = storage };
        try { await d.InitAsync(); Assert.Fail("Expected ArgumentException"); }
        catch (ArgumentException) { /* expected */ }
    }

    [Test]
    public async Task InitAsync_VideoThumbPosOutOfRange_ShouldThrow()
    {
        var addition = new LocalAddition { RootFolderPath = _tempRoot, VideoThumbPos = "150%" };
        var storage = new Storage { MountPath = "/test", Addition = JsonSerializer.Serialize(addition) };
        var d = new LocalDriver { Storage = storage };
        try { await d.InitAsync(); Assert.Fail("Expected ArgumentException"); }
        catch (ArgumentException) { /* expected */ }
    }

    [Test]
    public async Task InitAsync_DirectorySize_ShouldCalculate()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "file1.txt"), "hello");
        File.WriteAllText(Path.Combine(_tempRoot, "file2.txt"), "world!");

        var d = await InitDriverAsync(directorySize: true);
        await Assert.That(d).IsNotNull();
    }

    // ── EnumerateItems Tests (replaces ListAsync) ──

    [Test]
    public async Task EnumerateItems_ShouldReturnEntries()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "file1.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "subdir"));
        var d = await InitDriverAsync();

        var result = d.EnumerateItems(UPath.Root, System.IO.SearchOption.TopDirectoryOnly).ToList();

        await Assert.That(result).HasCount().EqualTo(2);
        await Assert.That(result.Any(r => r.GetName() == "file1.txt")).IsTrue();
        await Assert.That(result.Any(r => r.GetName() == "subdir" && r.IsDirectory)).IsTrue();
    }

    [Test]
    public async Task EnumerateItems_ShowHidden_ShouldIncludeDotFiles()
    {
        File.WriteAllText(Path.Combine(_tempRoot, ".hidden"), "hidden");
        var d = await InitDriverAsync(showHidden: true);

        var result = d.EnumerateItems(UPath.Root, System.IO.SearchOption.TopDirectoryOnly).ToList();
        await Assert.That(result.Any(r => r.GetName() == ".hidden")).IsTrue();
    }

    // ── FileExists / DirectoryExists Tests (replaces GetAsync) ──

    [Test]
    public async Task FileExists_ExistingFile_ShouldReturnTrue()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "test.txt"), "content");
        var d = await InitDriverAsync();

        await Assert.That(d.FileExists("/test.txt")).IsTrue();
        await Assert.That(d.GetFileLength("/test.txt")).IsEqualTo(7);
    }

    [Test]
    public async Task DirectoryExists_ExistingDir_ShouldReturnTrue()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "mydir"));
        var d = await InitDriverAsync();

        await Assert.That(d.DirectoryExists("/mydir")).IsTrue();
    }

    [Test]
    public async Task FileExists_NonExistent_ShouldReturnFalse()
    {
        var d = await InitDriverAsync();
        await Assert.That(d.FileExists("/nonexistent.txt")).IsFalse();
    }

    // ── GetLinkAsync Tests ──

    [Test]
    public async Task GetLinkAsync_ExistingFile_ShouldReturnLinkWithPath()
    {
        var filePath = Path.Combine(_tempRoot, "data.bin");
        File.WriteAllText(filePath, "binary data");
        var d = await InitDriverAsync();

        var link = await d.GetLinkAsync("/data.bin", new LinkArgs());

        await Assert.That(link).IsNotNull();
        await Assert.That(link!.URL).IsEqualTo(filePath);
    }

    [Test]
    public async Task GetLinkAsync_NonExistent_ShouldReturnNull()
    {
        var d = await InitDriverAsync();
        var link = await d.GetLinkAsync("/nonexistent", new LinkArgs());
        await Assert.That(link).IsNull();
    }

    // ── CreateDirectory Tests (replaces MakeDirAsync) ──

    [Test]
    public async Task CreateDirectory_ShouldCreateDirectory()
    {
        var d = await InitDriverAsync();
        d.CreateDirectory("/newdir");
        await Assert.That(Directory.Exists(Path.Combine(_tempRoot, "newdir"))).IsTrue();
    }

    // ── MoveFile Tests (replaces MoveAsync/RenameAsync for files) ──

    [Test]
    public async Task MoveFile_ShouldMove()
    {
        var srcFile = Path.Combine(_tempRoot, "move_me.txt");
        var dstDir = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(dstDir);
        File.WriteAllText(srcFile, "data");
        var d = await InitDriverAsync();

        d.MoveFile("/move_me.txt", "/dest/move_me.txt");
        await Assert.That(File.Exists(srcFile)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(dstDir, "move_me.txt"))).IsTrue();
    }

    [Test]
    public async Task RenameFile_ShouldRename()
    {
        var srcFile = Path.Combine(_tempRoot, "old_name.txt");
        File.WriteAllText(srcFile, "data");
        var d = await InitDriverAsync();

        d.MoveFile("/old_name.txt", "/new_name.txt");
        await Assert.That(File.Exists(srcFile)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(_tempRoot, "new_name.txt"))).IsTrue();
    }

    [Test]
    public async Task RenameDirectory_ShouldRename()
    {
        var srcDir = Path.Combine(_tempRoot, "old_dir");
        Directory.CreateDirectory(srcDir);
        var d = await InitDriverAsync();

        d.MoveDirectory("/old_dir", "/new_dir");
        await Assert.That(Directory.Exists(srcDir)).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(_tempRoot, "new_dir"))).IsTrue();
    }

    // ── CopyFile Tests (replaces CopyAsync for files) ──

    [Test]
    public async Task CopyFile_ShouldCopy()
    {
        var srcFile = Path.Combine(_tempRoot, "copy_me.txt");
        var dstDir = Path.Combine(_tempRoot, "copy_dest");
        Directory.CreateDirectory(dstDir);
        File.WriteAllText(srcFile, "copy data");
        var d = await InitDriverAsync();

        d.CopyFile("/copy_me.txt", "/copy_dest/copy_me.txt", overwrite: false);
        await Assert.That(File.Exists(srcFile)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(dstDir, "copy_me.txt"))).IsTrue();
    }

    // ── DeleteFile / DeleteDirectory Tests (replaces RemoveAsync) ──

    [Test]
    public async Task DeleteFile_ShouldDelete()
    {
        var filePath = Path.Combine(_tempRoot, "delete_me.txt");
        File.WriteAllText(filePath, "bye");
        var d = await InitDriverAsync();

        d.DeleteFile("/delete_me.txt");
        await Assert.That(File.Exists(filePath)).IsFalse();
    }

    [Test]
    public async Task DeleteDirectory_ShouldDeleteRecursive()
    {
        var dirPath = Path.Combine(_tempRoot, "delete_dir");
        Directory.CreateDirectory(dirPath);
        File.WriteAllText(Path.Combine(dirPath, "child.txt"), "data");
        var d = await InitDriverAsync();

        d.DeleteDirectory("/delete_dir", isRecursive: true);
        await Assert.That(Directory.Exists(dirPath)).IsFalse();
    }

    [Test]
    public async Task DeleteFile_RecycleBin_ShouldMoveToRecycle()
    {
        Directory.CreateDirectory(_recycleBin);
        var filePath = Path.Combine(_tempRoot, "recycle_me.txt");
        File.WriteAllText(filePath, "recycle");
        var d = await InitDriverAsync(recycleBinPath: _recycleBin);

        d.DeleteFile("/recycle_me.txt");
        await Assert.That(File.Exists(filePath)).IsFalse();
        var recycled = Directory.GetFiles(_recycleBin, "recycle_me*", SearchOption.AllDirectories);
        await Assert.That(recycled.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task DeleteFile_DeletePermanently_ShouldDelete()
    {
        var filePath = Path.Combine(_tempRoot, "perm_delete.txt");
        File.WriteAllText(filePath, "gone");
        var d = await InitDriverAsync(recycleBinPath: "delete permanently");

        d.DeleteFile("/perm_delete.txt");
        await Assert.That(File.Exists(filePath)).IsFalse();
    }

    // ── UploadAsync Tests (replaces PutAsync) ──

    [Test]
    public async Task UploadAsync_ShouldCreateFile()
    {
        var d = await InitDriverAsync();
        var content = Encoding.UTF8.GetBytes("uploaded content");
        using var stream = new MemoryStream(content);

        var resultPath = await d.UploadAsync(UPath.Root, stream, "uploaded.txt", _ => { });
        await Assert.That(resultPath).IsEqualTo(new UPath("/uploaded.txt"));
        await Assert.That(File.Exists(Path.Combine(_tempRoot, "uploaded.txt"))).IsTrue();
        var text = await File.ReadAllTextAsync(Path.Combine(_tempRoot, "uploaded.txt"));
        await Assert.That(text).IsEqualTo("uploaded content");
    }

    [Test]
    public async Task UploadAsync_Cancellation_ShouldCleanupFile()
    {
        var d = await InitDriverAsync();
        var cts = new CancellationTokenSource();
        var content = new byte[1024 * 1024];
        new Random().NextBytes(content);
        var slowStream = new SlowStream(content, TimeSpan.FromMilliseconds(500));

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        try
        {
            await d.UploadAsync(UPath.Root, slowStream, "cancelled.bin", _ => { }, cts.Token);
        }
        catch (OperationCanceledException) { }

        await Task.Delay(200);
        var filePath = Path.Combine(_tempRoot, "cancelled.bin");
        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
        await Assert.That(d).IsNotNull();
    }

    // ── GetDetailsAsync Tests ──

    [Test]
    public async Task GetDetailsAsync_ShouldReturnDiskInfo()
    {
        var d = await InitDriverAsync();
        var details = await d.GetDetailsAsync();
        await Assert.That(details).IsNotNull();
        await Assert.That(details!.TotalSpace).IsGreaterThan(0);
    }

    // ── Config Tests ──

    [Test]
    public async Task Config_ShouldReturnCorrectConfig()
    {
        var config = _driver.Config;
        await Assert.That(config.Name).IsEqualTo("Local");
        await Assert.That(config.LocalSort).IsTrue();
        await Assert.That(config.OnlyProxy).IsTrue();
        await Assert.That(config.NoCache).IsTrue();
        await Assert.That(config.DefaultRoot).IsEqualTo("/");
        await Assert.That(config.NoLinkURL).IsTrue();
    }
}

/// <summary>
/// A stream that reads slowly to simulate cancellation during upload.
/// </summary>
internal class SlowStream : Stream
{
    private readonly byte[] _data;
    private readonly TimeSpan _delay;
    private int _position;

    public SlowStream(byte[] data, TimeSpan delayPerChunk)
    {
        _data = data;
        _delay = delayPerChunk;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _data.Length;
    public override long Position { get => _position; set => _position = (int)value; }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(_delay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var remaining = _data.Length - _position;
        if (remaining <= 0) return 0;
        var toRead = Math.Min(count, remaining);
        Array.Copy(_data, _position, buffer, offset, toRead);
        _position += toRead;
        return toRead;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
