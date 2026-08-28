using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 文件对象更新钩子的发布/订阅注册表。镜像 OpenList Go 的 <c>internal/op</c> 包：
/// <c>RegisterObjsUpdateHook</c> 注册、<c>HandleObjsUpdateHook</c> 发布。
/// <para>
/// 文件系统写操作（上传/建目录/重命名/移动/复制/删除/解压）完成后，由
/// <see cref="IFileSystemService"/> 发布受影响目录的最新条目列表；订阅者（搜索索引等）
/// 据此做增量更新。发布与订阅相互解耦，避免 IFileSystemService 与 ISearchService
/// 之间的循环依赖。
/// </para>
/// </summary>
public interface IObjsUpdateHooks
{
    /// <summary>注册一个目录更新处理函数（父目录 + 最新条目列表）。</summary>
    void Subscribe(Func<string, IReadOnlyList<Obj>, CancellationToken, Task> dirUpdateHandler,
        Func<string, CancellationToken, Task>? pathRemovedHandler = null);

    /// <summary>发布某目录的最新条目列表，触发所有订阅者做增量更新。</summary>
    Task PublishDirectoryUpdateAsync(string parent, IReadOnlyList<Obj> entries, CancellationToken ct = default);

    /// <summary>发布路径被删除事件（含整棵子树），触发订阅者清理索引。</summary>
    Task PublishPathRemovedAsync(string path, CancellationToken ct = default);
}
