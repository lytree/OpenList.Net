using System.Collections.Concurrent;

namespace OpenList.Desktop.Services;

/// <summary>
/// 一条日志事件：来源 + 内容 + 级别。
///
/// <para><b>来源（Source）</b>用语言无关的稳定标识（<c>core</c> / <c>rclone</c> /
/// <c>desktop</c> / <c>dashboard</c> / <c>settings</c> / <c>tray</c> / <c>update</c>），
/// 这样日志页的来源筛选不受界面语言影响，显示时统一大写。</para>
///
/// <para><b>级别（Level）</b>为 INFO / WARN / ERROR / DEBUG。核心子进程的 stdout 只有纯文本，
/// 经 <see cref="LogEventBus.Info"/> 进来时级别为 INFO，再由日志页按关键字（fail/error/警告…）
/// 推断是否上调。</para>
/// </summary>
public sealed record LogEvent(DateTimeOffset Timestamp, string Source, string Message, string Level = LogLevels.Info)
{
    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{Source}] {Message}";
}

/// <summary>日志级别常量（避免散落的字符串字面量）。</summary>
public static class LogLevels
{
    public const string Info = "INFO";
    public const string Warn = "WARN";
    public const string Error = "ERROR";
    public const string Debug = "DEBUG";
}

/// <summary>日志来源标识。日志页按来源筛选时用这里的大写形态比对。</summary>
public static class LogSources
{
    public const string Core = "core";
    public const string Rclone = "rclone";
    public const string Desktop = "desktop";
    public const string Dashboard = "dashboard";
    public const string Settings = "settings";
    public const string Tray = "tray";
    public const string Update = "update";
    public const string Mount = "mount";

    /// <summary>日志表格里显示的形态（统一大写，与界面语言无关）。</summary>
    public const string CoreUpper = "CORE";
    public const string RcloneUpper = "RCLONE";
    public const string DesktopUpper = "DESKTOP";
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

    public void Info(string source, string message) =>
        Emit(new LogEvent(DateTimeOffset.Now, source, message, LogLevels.Info));

    public void Warn(string source, string message) =>
        Emit(new LogEvent(DateTimeOffset.Now, source, message, LogLevels.Warn));

    public void Error(string source, string message) =>
        Emit(new LogEvent(DateTimeOffset.Now, source, message, LogLevels.Error));

    public void Emit(LogEvent evt)
    {
        _recent.Enqueue(evt);
        while (_recent.Count > Capacity && _recent.TryDequeue(out _)) { }
        try { OnEvent?.Invoke(evt); } catch { /* 订阅方异常不影响发射方 */ }
    }

    /// <summary>启动时回放缓冲区里的历史日志（供页面初始化）。</summary>
    public IReadOnlyList<LogEvent> Snapshot() => [.. _recent];
}
