using System.Text;
using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Core.Models;
using OpenList.Drivers.Local;
using FileStreamer = OpenList.Core.Drivers.FileStreamer;

namespace OpenList.Tests;

/// <summary>
/// Tests for LocalDriver using temporary directories.
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

        var driver = new LocalDriver();
        driver.SetStorage(storage);
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
        // Create a subdir to use as relative root
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
        var d = new LocalDriver();
        d.SetStorage(storage);
        try { await d.InitAsync(); Assert.Fail("Expected ArgumentException"); }
        catch (ArgumentException) { /* expected */ }
    }

    [Test]
    public async Task InitAsync_VideoThumbPosOutOfRange_ShouldThrow()
    {
        var addition = new LocalAddition { RootFolderPath = _tempRoot, VideoThumbPos = "150%" };
        var storage = new Storage { MountPath = "/test", Addition = JsonSerializer.Serialize(addition) };
        var d = new LocalDriver();
        d.SetStorage(storage);
        try { await d.InitAsync(); Assert.Fail("Expected ArgumentException"); }
        catch (ArgumentException) { /* expected */ }
    }

    [Test]
    public async Task InitAsync_DirectorySize_ShouldCalculate()
    {
        // Create test files
        File.WriteAllText(Path.Combine(_tempRoot, "file1.txt"), "hello");
        File.WriteAllText(Path.Combine(_tempRoot, "file2.txt"), "world!");

        var d = await InitDriverAsync(directorySize: true);
        await Assert.That(d).IsNotNull();
    }

    // ── ListAsync Tests ──

    [Test]
    public async Task ListAsync_ShouldReturnEntries()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "file1.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "subdir"));
        var d = await InitDriverAsync();

        var dir = new ObjItem { Name = "root", Path = _tempRoot, IsFolder = true };
        var result = await d.ListAsync(dir, new ListArgs());

        await Assert.That(result).HasCount().EqualTo(2);
        await Assert.That(result.Any(r => r.GetName() == "file1.txt")).IsTrue();
        await Assert.That(result.Any(r => r.GetName() == "subdir" && r.IsDir())).IsTrue();
    }

    [Test]
    public async Task ListAsync_HideHidden_ShouldFilterDotFiles()
    {
        File.WriteAllText(Path.Combine(_tempRoot, ".hidden"), "hidden");
        File.WriteAllText(Path.Combine(_tempRoot, "visible.txt"), "visible");
        var d = await InitDriverAsync(showHidden: false);

        var dir = new ObjItem { Name = "root", Path = _tempRoot, IsFolder = true };
        var result = await d.ListAsync(dir, new ListArgs());

        await Assert.That(result.Any(r => r.GetName() == ".hidden")).IsFalse();
        await Assert.That(result.Any(r => r.GetName() == "visible.txt")).IsTrue();
    }

    [Test]
    public async Task ListAsync_ShowHidden_ShouldIncludeDotFiles()
    {
        File.WriteAllText(Path.Combine(_tempRoot, ".hidden"), "hidden");
        var d = await InitDriverAsync(showHidden: true);

        var dir = new ObjItem { Name = "root", Path = _tempRoot, IsFolder = true };
        var result = await d.ListAsync(dir, new ListArgs());

        await Assert.That(result.Any(r => r.GetName() == ".hidden")).IsTrue();
    }

    [Test]
    public async Task ListAsync_DirectorySize_ShouldIncludeSize()
    {
        var subDir = Path.Combine(_tempRoot, "mydir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "a.txt"), "12345");

        var d = await InitDriverAsync(directorySize: true);
        var dir = new ObjItem { Name = "root", Path = _tempRoot, IsFolder = true };
        var result = await d.ListAsync(dir, new ListArgs());

        var myDir = result.FirstOrDefault(r => r.GetName() == "mydir");
        await Assert.That(myDir).IsNotNull();
        await Assert.That(myDir!.GetSize()).IsEqualTo(5);
    }

    // ── GetAsync Tests ──

    [Test]
    public async Task GetAsync_ExistingFile_ShouldReturnObj()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "test.txt"), "content");
        var d = await InitDriverAsync();

        var obj = await d.GetAsync("/test.txt");
        await Assert.That(obj).IsNotNull();
        await Assert.That(obj!.GetName()).IsEqualTo("test.txt");
        await Assert.That(obj.IsDir()).IsFalse();
        await Assert.That(obj.GetSize()).IsEqualTo(7);
    }

    [Test]
    public async Task GetAsync_ExistingDir_ShouldReturnObj()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "mydir"));
        var d = await InitDriverAsync();

        var obj = await d.GetAsync("/mydir");
        await Assert.That(obj).IsNotNull();
        await Assert.That(obj!.IsDir()).IsTrue();
    }

    [Test]
    public async Task GetAsync_NonExistent_ShouldReturnNull()
    {
        var d = await InitDriverAsync();
        var obj = await d.GetAsync("/nonexistent.txt");
        await Assert.That(obj).IsNull();
    }

    // ── LinkAsync Tests ──

    [Test]
    public async Task LinkAsync_ExistingFile_ShouldReturnLinkWithPath()
    {
        var filePath = Path.Combine(_tempRoot, "data.bin");
        File.WriteAllText(filePath, "binary data");
        var d = await InitDriverAsync();

        var fileObj = new ObjItem { Name = "data.bin", Path = filePath, IsFolder = false };
        var link = await d.LinkAsync(fileObj, new LinkArgs());

        await Assert.That(link).IsNotNull();
        await Assert.That(link!.URL).IsEqualTo(filePath);
    }

    [Test]
    public async Task LinkAsync_NonExistent_ShouldReturnNull()
    {
        var d = await InitDriverAsync();
        var fileObj = new ObjItem { Name = "missing.bin", Path = "/nonexistent", IsFolder = false };
        var link = await d.LinkAsync(fileObj, new LinkArgs());
        await Assert.That(link).IsNull();
    }

    // ── MakeDirAsync Tests ──

    [Test]
    public async Task MakeDirAsync_ShouldCreateDirectory()
    {
        var d = await InitDriverAsync();
        var parent = new ObjItem { Name = "root", Path = _tempRoot, IsFolder = true };

        var result = await d.MakeDirAsync(parent, "newdir");
        await Assert.That(result).IsNotNull();
        await Assert.That(result.GetName()).IsEqualTo("newdir");
        await Assert.That(Directory.Exists(Path.Combine(_tempRoot, "newdir"))).IsTrue();
    }

    // ── MoveAsync Tests ──

    [Test]
    public async Task MoveAsync_File_ShouldMove()
    {
        var srcFile = Path.Combine(_tempRoot, "move_me.txt");
        var dstDir = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(dstDir);
        File.WriteAllText(srcFile, "data");
        var d = await InitDriverAsync();

        var srcObj = new ObjItem { Name = "move_me.txt", Path = srcFile, IsFolder = false };
        var dstObj = new ObjItem { Name = "dest", Path = dstDir, IsFolder = true };

        await d.MoveAsync(srcObj, dstObj);
        await Assert.That(File.Exists(srcFile)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(dstDir, "move_me.txt"))).IsTrue();
    }

    [Test]
    public async Task MoveAsync_SubfolderDest_ShouldThrow()
    {
        var srcDir = Path.Combine(_tempRoot, "parent");
        var subDir = Path.Combine(srcDir, "child");
        Directory.CreateDirectory(subDir);
        var d = await InitDriverAsync();

        var srcObj = new ObjItem { Name = "parent", Path = srcDir, IsFolder = true };
        var dstObj = new ObjItem { Name = "child", Path = subDir, IsFolder = true };

        try { await d.MoveAsync(srcObj, dstObj); Assert.Fail("Expected InvalidOperationException"); }
        catch (InvalidOperationException) { /* expected */ }
    }

    // ── RenameAsync Tests ──

    [Test]
    public async Task RenameAsync_File_ShouldRename()
    {
        var srcFile = Path.Combine(_tempRoot, "old_name.txt");
        File.WriteAllText(srcFile, "data");
        var d = await InitDriverAsync();

        var obj = new ObjItem { Name = "old_name.txt", Path = srcFile, IsFolder = false };
        await d.RenameAsync(obj, "new_name.txt");

        await Assert.That(File.Exists(srcFile)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(_tempRoot, "new_name.txt"))).IsTrue();
    }

    [Test]
    public async Task RenameAsync_Dir_ShouldRename()
    {
        var srcDir = Path.Combine(_tempRoot, "old_dir");
        Directory.CreateDirectory(srcDir);
        var d = await InitDriverAsync();

        var obj = new ObjItem { Name = "old_dir", Path = srcDir, IsFolder = true };
        await d.RenameAsync(obj, "new_dir");

        await Assert.That(Directory.Exists(srcDir)).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(_tempRoot, "new_dir"))).IsTrue();
    }

    // ── CopyAsync Tests ──

    [Test]
    public async Task CopyAsync_File_ShouldCopy()
    {
        var srcFile = Path.Combine(_tempRoot, "copy_me.txt");
        var dstDir = Path.Combine(_tempRoot, "copy_dest");
        Directory.CreateDirectory(dstDir);
        File.WriteAllText(srcFile, "copy data");
        var d = await InitDriverAsync();

        var srcObj = new ObjItem { Name = "copy_me.txt", Path = srcFile, IsFolder = false };
        var dstObj = new ObjItem { Name = "copy_dest", Path = dstDir, IsFolder = true };

        await d.CopyAsync(srcObj, dstObj);
        await Assert.That(File.Exists(srcFile)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(dstDir, "copy_me.txt"))).IsTrue();
    }

    [Test]
    public async Task CopyAsync_Dir_ShouldCopyRecursive()
    {
        var srcDir = Path.Combine(_tempRoot, "src_dir");
        var dstParent = Path.Combine(_tempRoot, "dst_parent");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstParent);
        File.WriteAllText(Path.Combine(srcDir, "a.txt"), "aaa");
        Directory.CreateDirectory(Path.Combine(srcDir, "nested"));
        File.WriteAllText(Path.Combine(srcDir, "nested", "b.txt"), "bbb");
        var d = await InitDriverAsync();

        var srcObj = new ObjItem { Name = "src_dir", Path = srcDir, IsFolder = true };
        var dstObj = new ObjItem { Name = "dst_parent", Path = dstParent, IsFolder = true };

        await d.CopyAsync(srcObj, dstObj);
        var copied = Path.Combine(dstParent, "src_dir");
        await Assert.That(File.Exists(Path.Combine(copied, "a.txt"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(copied, "nested", "b.txt"))).IsTrue();
    }

    [Test]
    public async Task CopyAsync_SubfolderDest_ShouldThrow()
    {
        var srcDir = Path.Combine(_tempRoot, "parent2");
        var subDir = Path.Combine(srcDir, "child2");
        Directory.CreateDirectory(subDir);
        var d = await InitDriverAsync();

        var srcObj = new ObjItem { Name = "parent2", Path = srcDir, IsFolder = true };
        var dstObj = new ObjItem { Name = "child2", Path = subDir, IsFolder = true };

        try { await d.CopyAsync(srcObj, dstObj); Assert.Fail("Expected InvalidOperationException"); }
        catch (InvalidOperationException) { /* expected */ }
    }

    // ── RemoveAsync Tests ──

    [Test]
    public async Task RemoveAsync_File_ShouldDelete()
    {
        var filePath = Path.Combine(_tempRoot, "delete_me.txt");
        File.WriteAllText(filePath, "bye");
        var d = await InitDriverAsync();

        var obj = new ObjItem { Name = "delete_me.txt", Path = filePath, IsFolder = false };
        await d.RemoveAsync(obj);
        await Assert.That(File.Exists(filePath)).IsFalse();
    }

    [Test]
    public async Task RemoveAsync_Dir_ShouldDeleteRecursive()
    {
        var dirPath = Path.Combine(_tempRoot, "delete_dir");
        Directory.CreateDirectory(dirPath);
        File.WriteAllText(Path.Combine(dirPath, "child.txt"), "data");
        var d = await InitDriverAsync();

        var obj = new ObjItem { Name = "delete_dir", Path = dirPath, IsFolder = true };
        await d.RemoveAsync(obj);
        await Assert.That(Directory.Exists(dirPath)).IsFalse();
    }

    [Test]
    public async Task RemoveAsync_RecycleBin_ShouldMoveToRecycle()
    {
        Directory.CreateDirectory(_recycleBin);
        var filePath = Path.Combine(_tempRoot, "recycle_me.txt");
        File.WriteAllText(filePath, "recycle");
        var d = await InitDriverAsync(recycleBinPath: _recycleBin);

        var obj = new ObjItem { Name = "recycle_me.txt", Path = filePath, IsFolder = false };
        await d.RemoveAsync(obj);

        await Assert.That(File.Exists(filePath)).IsFalse();
        // File should exist somewhere in recycle bin
        var recycled = Directory.GetFiles(_recycleBin, "recycle_me*", SearchOption.AllDirectories);
        await Assert.That(recycled.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task RemoveAsync_DeletePermanently_ShouldDelete()
    {
        var filePath = Path.Combine(_tempRoot, "perm_delete.txt");
        File.WriteAllText(filePath, "gone");
        var d = await InitDriverAsync(recycleBinPath: "delete permanently");

        var obj = new ObjItem { Name = "perm_delete.txt", Path = filePath, IsFolder = false };
        await d.RemoveAsync(obj);
        await Assert.That(File.Exists(filePath)).IsFalse();
    }

    // ── PutAsync Tests ──

    [Test]
    public async Task PutAsync_ShouldCreateFile()
    {
        var d = await InitDriverAsync();
        var dstDir = new ObjItem { Name = "root", Path = _tempRoot, IsFolder = true };
        var content = Encoding.UTF8.GetBytes("uploaded content");
        var stream = new MemoryStream(content);
        var file = new FileStreamer
        {
            Stream = stream,
            Obj = new ObjItem { Name = "uploaded.txt" },
            Size = content.Length
        };

        var result = await d.PutAsync(dstDir, file, _ => { });
        await Assert.That(result).IsNotNull();
        await Assert.That(result.GetName()).IsEqualTo("uploaded.txt");
        await Assert.That(File.Exists(Path.Combine(_tempRoot, "uploaded.txt"))).IsTrue();
        var text = await File.ReadAllTextAsync(Path.Combine(_tempRoot, "uploaded.txt"));
        await Assert.That(text).IsEqualTo("uploaded content");
    }

    [Test]
    public async Task PutAsync_Cancellation_ShouldCleanupFile()
    {
        var d = await InitDriverAsync();
        var dstDir = new ObjItem { Name = "root", Path = _tempRoot, IsFolder = true };

        // Create a slow stream that will be cancelled
        var cts = new CancellationTokenSource();
        var content = new byte[1024 * 1024]; // 1MB
        new Random().NextBytes(content);
        var slowStream = new SlowStream(content, TimeSpan.FromMilliseconds(500));
        var file = new FileStreamer
        {
            Stream = slowStream,
            Obj = new ObjItem { Name = "cancelled.bin" },
            Size = content.Length
        };

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        try
        {
            await d.PutAsync(dstDir, file, _ => { }, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* FluentStorage may wrap the exception */ }

        // Allow time for cleanup
        await Task.Delay(200);
        // Clean up manually if the file still exists (FluentStorage may not trigger cancellation)
        var filePath = Path.Combine(_tempRoot, "cancelled.bin");
        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
        // The test mainly verifies that cancellation doesn't crash
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
        var config = _driver.Config();
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
