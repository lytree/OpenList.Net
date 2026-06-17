using System.Reflection;
using OpenList.Core.Drivers;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

public class FileSystemServiceTests
{
    // ── Test GetParentPath (private static) via reflection ──

    private static string? InvokeGetParentPath(string path)
    {
        var method = typeof(FileSystemService).GetMethod("GetParentPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        return method?.Invoke(null, [path]) as string;
    }

    [Test]
    public async Task GetParentPath_RootPath_ShouldReturnRoot()
    {
        await Assert.That(InvokeGetParentPath("/")).IsEqualTo("/");
    }

    [Test]
    public async Task GetParentPath_SingleSegment_ShouldReturnRoot()
    {
        await Assert.That(InvokeGetParentPath("/data")).IsEqualTo("/");
    }

    [Test]
    public async Task GetParentPath_MultiSegment_ShouldReturnParent()
    {
        await Assert.That(InvokeGetParentPath("/data/sub/file.txt")).IsEqualTo("/data/sub");
    }

    [Test]
    public async Task GetParentPath_TrailingSlash_ShouldTrimFirst()
    {
        await Assert.That(InvokeGetParentPath("/data/sub/")).IsEqualTo("/data");
    }

    // ── Test SortFiles (private static) via reflection ──

    private static void InvokeSortFiles(List<IObj> objs, string? orderBy, string? orderDirection)
    {
        var method = typeof(FileSystemService).GetMethod("SortFiles",
            BindingFlags.NonPublic | BindingFlags.Static);
        method?.Invoke(null, [objs, orderBy, orderDirection]);
    }

    [Test]
    public async Task SortFiles_ByName_Ascending()
    {
        var objs = new List<IObj>
        {
            new ObjItem { Name = "charlie" },
            new ObjItem { Name = "alpha" },
            new ObjItem { Name = "bravo" }
        };

        InvokeSortFiles(objs, "name", "asc");

        await Assert.That(objs[0].GetName()).IsEqualTo("alpha");
        await Assert.That(objs[1].GetName()).IsEqualTo("bravo");
        await Assert.That(objs[2].GetName()).IsEqualTo("charlie");
    }

    [Test]
    public async Task SortFiles_ByName_Descending()
    {
        var objs = new List<IObj>
        {
            new ObjItem { Name = "alpha" },
            new ObjItem { Name = "charlie" },
            new ObjItem { Name = "bravo" }
        };

        InvokeSortFiles(objs, "name", "desc");

        await Assert.That(objs[0].GetName()).IsEqualTo("charlie");
        await Assert.That(objs[1].GetName()).IsEqualTo("bravo");
        await Assert.That(objs[2].GetName()).IsEqualTo("alpha");
    }

    [Test]
    public async Task SortFiles_BySize_Ascending()
    {
        var objs = new List<IObj>
        {
            new ObjItem { Name = "big", Size = 1000 },
            new ObjItem { Name = "small", Size = 100 },
            new ObjItem { Name = "medium", Size = 500 }
        };

        InvokeSortFiles(objs, "size", "asc");

        await Assert.That(objs[0].GetSize()).IsEqualTo(100);
        await Assert.That(objs[1].GetSize()).IsEqualTo(500);
        await Assert.That(objs[2].GetSize()).IsEqualTo(1000);
    }

    [Test]
    public async Task SortFiles_ByModified_Ascending()
    {
        var objs = new List<IObj>
        {
            new ObjItem { Name = "newest", Modified = new DateTime(2024, 12, 1) },
            new ObjItem { Name = "oldest", Modified = new DateTime(2024, 1, 1) },
            new ObjItem { Name = "middle", Modified = new DateTime(2024, 6, 1) }
        };

        InvokeSortFiles(objs, "modified", "asc");

        await Assert.That(objs[0].GetName()).IsEqualTo("oldest");
        await Assert.That(objs[1].GetName()).IsEqualTo("middle");
        await Assert.That(objs[2].GetName()).IsEqualTo("newest");
    }

    [Test]
    public async Task SortFiles_NullOrderBy_ShouldNotSort()
    {
        var objs = new List<IObj>
        {
            new ObjItem { Name = "charlie" },
            new ObjItem { Name = "alpha" },
            new ObjItem { Name = "bravo" }
        };

        InvokeSortFiles(objs, null, "asc");

        // Order should remain unchanged
        await Assert.That(objs[0].GetName()).IsEqualTo("charlie");
        await Assert.That(objs[1].GetName()).IsEqualTo("alpha");
    }

    // ── Test ExtractFolder (private static) via reflection ──

    private static void InvokeExtractFolder(List<IObj> objs, string? extractFolder)
    {
        var method = typeof(FileSystemService).GetMethod("ExtractFolder",
            BindingFlags.NonPublic | BindingFlags.Static);
        method?.Invoke(null, [objs, extractFolder]);
    }

    [Test]
    public async Task ExtractFolder_Front_FoldersFirst()
    {
        var objs = new List<IObj>
        {
            new ObjItem { Name = "file1.txt", IsFolder = false },
            new ObjItem { Name = "dir1", IsFolder = true },
            new ObjItem { Name = "file2.txt", IsFolder = false },
            new ObjItem { Name = "dir2", IsFolder = true }
        };

        InvokeExtractFolder(objs, "front");

        await Assert.That(objs[0].IsDir()).IsTrue();
        await Assert.That(objs[1].IsDir()).IsTrue();
        await Assert.That(objs[2].IsDir()).IsFalse();
        await Assert.That(objs[3].IsDir()).IsFalse();
    }

    [Test]
    public async Task ExtractFolder_Back_FoldersLast()
    {
        var objs = new List<IObj>
        {
            new ObjItem { Name = "file1.txt", IsFolder = false },
            new ObjItem { Name = "dir1", IsFolder = true },
            new ObjItem { Name = "file2.txt", IsFolder = false },
            new ObjItem { Name = "dir2", IsFolder = true }
        };

        InvokeExtractFolder(objs, "back");

        await Assert.That(objs[0].IsDir()).IsFalse();
        await Assert.That(objs[1].IsDir()).IsFalse();
        await Assert.That(objs[2].IsDir()).IsTrue();
        await Assert.That(objs[3].IsDir()).IsTrue();
    }

    [Test]
    public async Task ExtractFolder_Null_ShouldNotChange()
    {
        var objs = new List<IObj>
        {
            new ObjItem { Name = "file1.txt", IsFolder = false },
            new ObjItem { Name = "dir1", IsFolder = true }
        };

        InvokeExtractFolder(objs, null);

        await Assert.That(objs[0].GetName()).IsEqualTo("file1.txt");
        await Assert.That(objs[1].GetName()).IsEqualTo("dir1");
    }

    // ── Test BuildArchiveTree (private static) via reflection ──

    private static List<ArchiveContentEntry>? InvokeBuildArchiveTree(List<ArchiveEntry> entries)
    {
        var method = typeof(FileSystemService).GetMethod("BuildArchiveTree",
            BindingFlags.NonPublic | BindingFlags.Static);
        return method?.Invoke(null, [entries]) as List<ArchiveContentEntry>;
    }

    [Test]
    public async Task BuildArchiveTree_EmptyList_ShouldReturnEmpty()
    {
        var result = InvokeBuildArchiveTree([]);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEmpty();
    }

    [Test]
    public async Task BuildArchiveTree_FlatEntries_ShouldReturnAsIs()
    {
        var entries = new List<ArchiveEntry>
        {
            new() { Name = "file1.txt", Size = 100 },
            new() { Name = "file2.txt", Size = 200 }
        };

        var result = InvokeBuildArchiveTree(entries);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!).HasCount().EqualTo(2);
    }

    [Test]
    public async Task BuildArchiveTree_PathEntries_ShouldBuildTree()
    {
        var entries = new List<ArchiveEntry>
        {
            new() { Name = "dir", IsDir = true, InnerPath = "dir" },
            new() { Name = "file.txt", Size = 100, InnerPath = "dir/file.txt" }
        };

        var result = InvokeBuildArchiveTree(entries);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!).HasCount().EqualTo(1);
        await Assert.That(result[0].Name).IsEqualTo("dir");
        await Assert.That(result[0].Children).HasCount().EqualTo(1);
        await Assert.That(result[0].Children[0].Name).IsEqualTo("file.txt");
    }
}
