using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 文件对象更新钩子注册表的默认实现（线程安全）。搜索索引等服务在启动时订阅，
/// 文件系统写操作完成后发布。单个处理函数的异常被隔离，不影响其它订阅者。
/// </summary>
public sealed class ObjsUpdateHooks : IObjsUpdateHooks
{
    private readonly object _lock = new();
    private Func<string, IReadOnlyList<Obj>, CancellationToken, Task> _dirHandler = static (_, _, _) => Task.CompletedTask;
    private Func<string, CancellationToken, Task> _removedHandler = static (_, _) => Task.CompletedTask;

    /// <inheritdoc />
    public void Subscribe(Func<string, IReadOnlyList<Obj>, CancellationToken, Task> dirUpdateHandler,
        Func<string, CancellationToken, Task>? pathRemovedHandler = null)
    {
        lock (_lock)
        {
            _dirHandler = dirUpdateHandler;
            if (pathRemovedHandler is not null)
                _removedHandler = pathRemovedHandler;
        }
    }

    /// <inheritdoc />
    public async Task PublishDirectoryUpdateAsync(string parent, IReadOnlyList<Obj> entries, CancellationToken ct = default)
    {
        Func<string, IReadOnlyList<Obj>, CancellationToken, Task> handler;
        lock (_lock) { handler = _dirHandler; }
        try { await handler(parent, entries, ct).ConfigureAwait(false); }
        catch { /* 单个订阅者失败不影响调用方 */ }
    }

    /// <inheritdoc />
    public async Task PublishPathRemovedAsync(string path, CancellationToken ct = default)
    {
        Func<string, CancellationToken, Task> handler;
        lock (_lock) { handler = _removedHandler; }
        try { await handler(path, ct).ConfigureAwait(false); }
        catch { /* 单个订阅者失败不影响调用方 */ }
    }
}
