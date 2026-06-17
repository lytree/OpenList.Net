using System.Collections.Concurrent;

namespace OpenList.Drivers.Local;

/// <summary>
/// Caches directory sizes for fast lookup. Mirrors Go's DirectoryMap (sync.Map based).
/// Uses iterative stack-based traversal to avoid stack overflow on deep trees.
/// </summary>
public class DirectoryMap
{
    public string Root { get; set; } = "";
    private readonly ConcurrentDictionary<string, DirectoryNode> _data = new();

    public bool Has(string path) => _data.ContainsKey(path);

    public DirectoryNode? Get(string path) => _data.GetValueOrDefault(path);

    public void Set(string path, DirectoryNode node) => _data[path] = node;

    public bool Delete(string path) => _data.TryRemove(path, out _);

    public void Clear() => _data.Clear();

    /// <summary>
    /// Iterative stack-based directory size calculation.
    /// Mirrors Go's CalculateDirSize with post-order traversal using a task stack.
    /// </summary>
    public long CalculateDirSize(string dirname)
    {
        var stack = new Stack<DirectoryTask>();
        stack.Push(new DirectoryTask { Path = dirname });

        while (stack.Count > 0)
        {
            var task = stack.Pop();

            if (task.Cache != null)
            {
                // Post-order: children have been processed, accumulate directory sizes
                long directorySum = 0;
                foreach (var childName in task.Cache.Children)
                {
                    var childPath = Path.Join(task.Path, childName);
                    var child = Get(childPath);
                    if (child != null)
                        directorySum += child.FileSum + child.DirectorySum;
                }

                Set(task.Path, new DirectoryNode
                {
                    FileSum = task.Cache.FileSum,
                    DirectorySum = directorySum,
                    Children = task.Cache.Children
                });
                continue;
            }

            // Pre-order: read directory, push subdirectories
            var entries = SafeReadDir(task.Path);
            if (entries == null) continue;

            long fileSum = 0;
            var children = new List<string>();
            var pendingChildren = new List<DirectoryTask>();

            foreach (var (name, isFolder, size) in entries)
            {
                if (isFolder)
                {
                    var fullpath = Path.Join(task.Path, name);
                    var existing = Get(fullpath);
                    if (existing != null)
                    {
                        // Already cached, will be used in post-order
                    }
                    else
                    {
                        pendingChildren.Add(new DirectoryTask { Path = fullpath });
                    }
                    children.Add(name);
                }
                else
                {
                    fileSum += size;
                }
            }

            if (pendingChildren.Count > 0)
            {
                // Push post-order task first (will be processed after children)
                stack.Push(new DirectoryTask
                {
                    Path = task.Path,
                    Cache = new DirectoryTaskCache { FileSum = fileSum, Children = children }
                });

                // Push children (processed before post-order task)
                foreach (var child in pendingChildren)
                    stack.Push(child);
            }
            else
            {
                // No pending children, compute directory sum from cached children
                long directorySum = 0;
                foreach (var childName in children)
                {
                    var childPath = Path.Join(task.Path, childName);
                    var child = Get(childPath);
                    if (child != null)
                        directorySum += child.FileSum + child.DirectorySum;
                }

                Set(task.Path, new DirectoryNode
                {
                    FileSum = fileSum,
                    DirectorySum = directorySum,
                    Children = children
                });
            }
        }

        var result = Get(dirname);
        return result != null ? result.FileSum + result.DirectorySum : 0;
    }

    /// <summary>
    /// Incremental update: re-read directory and recalculate.
    /// Mirrors Go's UpdateDirSize.
    /// </summary>
    public long UpdateDirSize(string dirname)
    {
        var existing = Get(dirname);
        if (existing == null) return 0;

        var entries = SafeReadDir(dirname);
        if (entries == null) return 0;

        long fileSum = 0;
        long directorySum = 0;
        var children = new List<string>();

        foreach (var (name, isFolder, size) in entries)
        {
            var fullpath = Path.Join(dirname, name);
            if (isFolder)
            {
                var child = Get(fullpath);
                if (child != null)
                    directorySum += child.FileSum + child.DirectorySum;
                else
                    directorySum += CalculateDirSize(fullpath);
                children.Add(name);
            }
            else
            {
                fileSum += size;
            }
        }

        // Remove stale child nodes
        foreach (var oldChild in existing.Children)
        {
            if (!children.Contains(oldChild))
                DeleteDirNode(Path.Join(dirname, oldChild));
        }

        existing.FileSum = fileSum;
        existing.DirectorySum = directorySum;
        existing.Children = children;

        return fileSum + directorySum;
    }

    /// <summary>
    /// Propagate size changes up to root. Mirrors Go's UpdateDirParents.
    /// </summary>
    public void UpdateDirParents(string dirname)
    {
        var parentPath = Path.GetDirectoryName(dirname);
        while (parentPath != null && (parentPath == Root || Root.StartsWith(parentPath + Path.DirectorySeparatorChar)))
        {
            var node = Get(parentPath);
            if (node != null)
            {
                long directorySum = 0;
                foreach (var childName in node.Children)
                {
                    var child = Get(Path.Join(parentPath, childName));
                    if (child != null)
                        directorySum += child.FileSum + child.DirectorySum;
                }
                node.DirectorySum = directorySum;
            }
            parentPath = Path.GetDirectoryName(parentPath);
        }
    }

    /// <summary>
    /// Recursively delete a node and all its descendants.
    /// Mirrors Go's DeleteDirNode.
    /// </summary>
    public void DeleteDirNode(string dirname)
    {
        var stack = new Stack<string>();
        stack.Push(dirname);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            var node = Get(current);
            if (node != null)
            {
                foreach (var childName in node.Children)
                    stack.Push(Path.Join(current, childName));
                Delete(current);
            }
        }
    }

    /// <summary>
    /// Clear and recalculate from root. Mirrors Go's RecalculateDirSize.
    /// </summary>
    public long RecalculateDirSize()
    {
        Clear();
        if (string.IsNullOrEmpty(Root)) return 0;
        return CalculateDirSize(Root);
    }

    private static List<(string Name, bool IsFolder, long Size)>? SafeReadDir(string path)
    {
        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists) return null;

            var result = new List<(string, bool, long)>();
            foreach (var entry in dirInfo.EnumerateFileSystemInfos())
            {
                var isFolder = entry is DirectoryInfo;
                long size = 0;
                if (!isFolder)
                {
                    try { size = ((FileInfo)entry).Length; } catch { }
                }
                result.Add((entry.Name, isFolder, size));
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}

public class DirectoryNode
{
    public long FileSum { get; set; }
    public long DirectorySum { get; set; }
    public List<string> Children { get; set; } = [];
}

internal class DirectoryTask
{
    public string Path { get; set; } = "";
    public DirectoryTaskCache? Cache { get; set; }
}

internal class DirectoryTaskCache
{
    public long FileSum { get; set; }
    public List<string> Children { get; set; } = [];
}
