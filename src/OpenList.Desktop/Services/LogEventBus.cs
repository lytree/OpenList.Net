using System.Collections.Concurrent;

namespace OpenList.Desktop.Services;

/// <summary>一条日志事件：来源 + 内容。</summary>
public sealed record LogEvent(DateTimeOffset Timestamp, string Source, string Message)
{
    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] [{Source}] {Message}";
}

/// <summary>
/// 进程内日志总线：核心子进程 stdout/stderr、桌面端自身事件、rclone 挂载输出
/// 都汇到这里，由日志页订阅显示。
/// </summary>
public sealed class LogEventBus
{
    private readonly ConcurrentQueue<LogEvent> _recent = new();
    private const int Capacity = 5000;

    /// <summary>事件在后台线程触发，订阅方需自行切 UI 线程。</summary>
    public event Action<LogEvent>? OnEvent;

    public void Info(string source, string message) => Emit(new LogEvent(DateTimeOffset.Now, source, message));
    public void Error(string source, string message) => Emit(new LogEvent(DateTimeOffset.Now, source, message));

    public void Emit(LogEvent evt)
    {
        _recent.Enqueue(evt);
        while (_recent.Count > Capacity && _recent.TryDequeue(out _)) { }
        try { OnEvent?.Invoke(evt); } catch { /* 订阅方异常不影响发射方 */ }
    }

    /// <summary>启动时回放缓冲区里的历史日志（供页面初始化）。</summary>
    public IReadOnlyList<LogEvent> Snapshot() => [.. _recent];
}
