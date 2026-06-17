using OpenList.Drivers.Local;

namespace OpenList.Tests;

public class DirectoryMapTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DirectoryMap _map;

    public DirectoryMapTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"dirmap_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _map = new DirectoryMap { Root = _tempRoot };
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    [Test]
    public async Task CalculateDirSize_EmptyDir_ShouldReturnZero()
    {
        var size = _map.CalculateDirSize(_tempRoot);
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task CalculateDirSize_WithFiles_ShouldReturnTotalSize()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "a.txt"), "12345");
        File.WriteAllText(Path.Combine(_tempRoot, "b.txt"), "67890");

        var size = _map.CalculateDirSize(_tempRoot);
        await Assert.That(size).IsEqualTo(10);
    }

    [Test]
    public async Task CalculateDirSize_NestedDirs_ShouldAccumulate()
    {
        var subDir = Path.Combine(_tempRoot, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(_tempRoot, "root.txt"), "12345");
        File.WriteAllText(Path.Combine(subDir, "nested.txt"), "67890abcde");

        var size = _map.CalculateDirSize(_tempRoot);
        await Assert.That(size).IsEqualTo(15);
    }

    [Test]
    public async Task UpdateDirSize_AddFile_ShouldIncrease()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "initial.txt"), "hello");
        _map.CalculateDirSize(_tempRoot);

        File.WriteAllText(Path.Combine(_tempRoot, "added.txt"), "world!");
        var size = _map.UpdateDirSize(_tempRoot);
        await Assert.That(size).IsEqualTo(11);
    }

    [Test]
    public async Task UpdateDirSize_RemoveFile_ShouldDecrease()
    {
        var filePath = Path.Combine(_tempRoot, "removable.txt");
        File.WriteAllText(filePath, "bye!");
        File.WriteAllText(Path.Combine(_tempRoot, "keeper.txt"), "stay");
        _map.CalculateDirSize(_tempRoot);

        File.Delete(filePath);
        var size = _map.UpdateDirSize(_tempRoot);
        await Assert.That(size).IsEqualTo(4);
    }

    [Test]
    public async Task UpdateDirParents_ShouldPropagateUp()
    {
        var subDir = Path.Combine(_tempRoot, "child");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "file.txt"), "data");
        _map.CalculateDirSize(_tempRoot);

        // Modify the file to be larger
        File.WriteAllText(Path.Combine(subDir, "file.txt"), "much more data here");
        _map.UpdateDirSize(subDir);
        _map.UpdateDirParents(subDir);

        var rootNode = _map.Get(_tempRoot);
        await Assert.That(rootNode).IsNotNull();
        await Assert.That(rootNode!.FileSum + rootNode.DirectorySum).IsGreaterThan(4);
    }

    [Test]
    public async Task DeleteDirNode_ShouldRemoveRecursively()
    {
        var subDir = Path.Combine(_tempRoot, "to_delete");
        var nestedDir = Path.Combine(subDir, "nested");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "file.txt"), "data");
        _map.CalculateDirSize(_tempRoot);

        await Assert.That(_map.Has(subDir)).IsTrue();
        await Assert.That(_map.Has(nestedDir)).IsTrue();

        _map.DeleteDirNode(subDir);

        await Assert.That(_map.Has(subDir)).IsFalse();
        await Assert.That(_map.Has(nestedDir)).IsFalse();
    }

    [Test]
    public async Task Has_Get_Set_Delete_Clear_BasicOperations()
    {
        var node = new DirectoryNode { FileSum = 100, DirectorySum = 50, Children = ["child1"] };
        _map.Set("/test/path", node);

        await Assert.That(_map.Has("/test/path")).IsTrue();
        await Assert.That(_map.Get("/test/path")).IsNotNull();
        await Assert.That(_map.Get("/test/path")!.FileSum).IsEqualTo(100);
        await Assert.That(_map.Get("/test/path")!.DirectorySum).IsEqualTo(50);

        _map.Delete("/test/path");
        await Assert.That(_map.Has("/test/path")).IsFalse();

        _map.Set("/a", new DirectoryNode());
        _map.Set("/b", new DirectoryNode());
        _map.Clear();
        await Assert.That(_map.Has("/a")).IsFalse();
        await Assert.That(_map.Has("/b")).IsFalse();
    }

    [Test]
    public async Task RecalculateDirSize_ShouldResetAndRecalculate()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "a.txt"), "12345");
        _map.CalculateDirSize(_tempRoot);

        File.WriteAllText(Path.Combine(_tempRoot, "b.txt"), "67890");
        _map.RecalculateDirSize();

        var node = _map.Get(_tempRoot);
        await Assert.That(node).IsNotNull();
        await Assert.That(node!.FileSum + node.DirectorySum).IsEqualTo(10);
    }

    [Test]
    public async Task CalculateDirSize_NonExistentDir_ShouldReturnZero()
    {
        var size = _map.CalculateDirSize(Path.Combine(_tempRoot, "nonexistent"));
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task Get_NonExistentPath_ShouldReturnNull()
    {
        var node = _map.Get("/nonexistent");
        await Assert.That(node).IsNull();
    }
}
