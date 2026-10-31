using System.Collections.Concurrent;

namespace OpenList.Infrastructure.Thumbnails;

/// <summary>
/// 缩略图两级缓存：进程内 LRU（小、热） + 磁盘（大、跨重启）。
/// <para>
/// 缓存键由 <see cref="ThumbnailService"/> 计算（含 path/size/mtime/尺寸/格式/质量/抽帧位置/生成器版本），
/// 这里只管存取，不理解键的含义。写盘用「临时文件 + 原子替换」，避免半截文件被后续请求读到。
/// </para>
/// <para>
/// 内存层刻意只存小图：`<img>` 请求的响应体通常 10–40KB，256 项就能覆盖一屏翻页；
/// 再大就有把工作集吃满的风险（进程重启后退化为直接读磁盘，行为仍然正确）。
/// </para>
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>内存层容量上限。</summary>
    private const long MemoryBudgetBytes = 48L * 1024 * 1024;

    /// <summary>内存层条目上限（防大量极小图把字典撑大）。</summary>
    private const int MemoryEntryLimit = 512;

    /// <summary>每写这么多次盘做一次容量检查 —— 不必每次都扫目录。</summary>
    private const int TrimEveryWrites = 64;

    private readonly ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _memoryOrder = new();
    private long _memoryBytes;
    private int _writesSinceTrim;

    /// <summary>从内存层取（未命中返回 null）。</summary>
    public byte[]? TryGetMemory(string key)
        => _memory.TryGetValue(key, out var data) ? data : null;

    /// <summary>写入内存层，并按容量/条目数淘汰最老的项。</summary>
    public void PutMemory(string key, byte[] data)
    {
        if (!_memory.TryAdd(key, data)) return;
        _memoryOrder.Enqueue(key);
        Interlocked.Add(ref _memoryBytes, data.Length);

        while ((Interlocked.Read(ref _memoryBytes) > MemoryBudgetBytes || _memory.Count > MemoryEntryLimit)
               && _memoryOrder.TryDequeue(out var oldest))
        {
            if (_memory.TryRemove(oldest, out var evicted))
                Interlocked.Add(ref _memoryBytes, -evicted.Length);
        }
    }

    /// <summary>把缓存键映射为磁盘路径：<c>{folder}/{key[0..2]}/{key}.{ext}</c>。</summary>
    /// <remarks>
    /// 用前两位分桶是必须的：单目录几十万文件时 ext4/NTFS 的 <c>readdir</c> 会明显退化，
    /// 而缩略图缓存的条目数是按「用户访问过的图片数」增长的。
    /// </remarks>
    public static string PathFor(string folder, string key, string extension)
    {
        var bucket = key.Length >= 2 ? key[..2] : "00";
        return Path.Combine(folder, bucket, $"{key}.{extension}");
    }

    /// <summary>读磁盘缓存；不存在或读失败返回 null（缓存坏了等价于没命中）。</summary>
    public async Task<byte[]?> ReadAsync(string folder, string key, string extension, CancellationToken ct)
    {
        var path = PathFor(folder, key, extension);
        try
        {
            if (!File.Exists(path)) return null;
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>写磁盘缓存；失败只记日志不影响请求（例如缓存目录被配到了只读盘）。</summary>
    /// <param name="folder">缓存根目录。</param>
    /// <param name="key">缓存键。</param>
    /// <param name="extension">文件扩展名（不带点）。</param>
    /// <param name="data">内容。</param>
    /// <param name="maxBytes">容量上限；超过时触发一次淘汰。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task WriteAsync(string folder, string key, string extension, byte[] data, long maxBytes, CancellationToken ct)
    {
        var path = PathFor(folder, key, extension);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 忽略 */ }
            return;
        }

        if (Interlocked.Increment(ref _writesSinceTrim) < TrimEveryWrites) return;
        Interlocked.Exchange(ref _writesSinceTrim, 0);
        await TrimAsync(folder, maxBytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 按容量淘汰：总量超过 <paramref name="maxBytes"/> 时，从「最久未修改」开始删。
    /// </summary>
    /// <remarks>
    /// 用 mtime 而不是 atime：Windows 默认关闭 NTFS 的最后访问时间更新，跨平台不可靠；
    /// 而缩略图一旦命中就不该再被改写，mtime 在这里正好等于「最近一次生成」。
    /// </remarks>
    public async Task TrimAsync(string folder, long maxBytes, CancellationToken ct)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= maxBytes) break;
                try
                {
                    total -= file.Length;
                    file.Delete();
                }
                catch { /* 单个文件删不掉不影响整体 */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 目录不可读时放弃本轮淘汰 */ }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
