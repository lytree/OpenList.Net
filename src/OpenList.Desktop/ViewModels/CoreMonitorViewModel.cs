using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenList.Desktop.Localization;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>监控曲线上的一个采样点（Value：CPU 为百分比、内存为 MB）。</summary>
public sealed record MetricPoint(int Index, double Value, string Time);

/// <summary>
/// 核心监控页：采集核心子进程（OpenList.Server）的 PID、运行时长、CPU 占用、
/// 内存（WorkingSet）、线程数、句柄数与 /ping 延迟，并绘制 CPU / 内存历史曲线。
/// </summary>
public partial class CoreMonitorViewModel : ViewModelBase
{
    private const int WindowSize = 120;

    private readonly OpenListCoreService _core;
    private readonly DispatcherTimer _timer;
    private readonly List<MetricPoint> _cpu = [];
    private readonly List<MetricPoint> _mem = [];

    private TimeSpan _lastCpuTime;
    private DateTime _lastSample;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = Localizer.Get("status.stopped");
    [ObservableProperty] private string _pidText = "--";
    [ObservableProperty] private string _uptimeText = "--";
    [ObservableProperty] private string _cpuText = "--";
    [ObservableProperty] private string _memText = "--";
    [ObservableProperty] private string _threadsText = "--";
    [ObservableProperty] private string _handlesText = "--";
    [ObservableProperty] private string _latencyText = "--";

    /// <summary>CPU 曲线（视图监听 PropertyChanged 重绘）。</summary>
    public IReadOnlyList<MetricPoint> CpuPoints => _cpu;

    /// <summary>内存曲线（视图监听 PropertyChanged 重绘）。</summary>
    public IReadOnlyList<MetricPoint> MemPoints => _mem;

    public CoreMonitorViewModel(OpenListCoreService core, AppSettings settings)
    {
        _core = core;
        _isRunning = core.Status == CoreStatus.Running;
        StatusText = Describe(core.Status);
        if (IsRunning) _lastSample = DateTime.UtcNow;
        core.StatusChanged += s => Dispatcher.UIThread.Post(() =>
        {
            IsRunning = s == CoreStatus.Running;
            StatusText = Describe(s);
            if (!IsRunning) ResetSeries();
        });

        // 采样节奏跟随心跳间隔设置（默认 10 秒）。此前是 1 秒定时 + %3 跳拍 = 3 秒一次，
        // 对"看趋势"这种用途过密，且每次都要 Refresh 进程对象、打一次 /ping。
        _timer = new DispatcherTimer { Interval = settings.HeartbeatInterval };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        // 设置页改心跳间隔后立即生效，不必重启桌面端
        settings.HeartbeatIntervalChanged += () => _timer.Interval = settings.HeartbeatInterval;
    }

    /// <summary>
    /// 页面可见性开关：监控页不在前台时停止采样。
    /// 由 <see cref="MainWindowViewModel"/> 在切换导航页时驱动。
    /// </summary>
    /// <param name="visible">监控页是否处于前台。</param>
    public void SetVisible(bool visible)
    {
        if (visible) _timer.Start();
        else _timer.Stop();
    }

    private static string Describe(CoreStatus s) =>
        Localizer.Get(NotificationService.CoreStatusKey(s));

    private void ResetSeries()
    {
        _cpu.Clear();
        _mem.Clear();
        _lastSample = default;
        _lastCpuTime = default;
        PidText = UptimeText = CpuText = MemText = ThreadsText = HandlesText = LatencyText = "--";
        OnPropertyChanged(nameof(CpuPoints));
        OnPropertyChanged(nameof(MemPoints));
    }

    private async Task TickAsync()
    {
        if (_core.Status != CoreStatus.Running) return;

        var proc = _core.CurrentProcess;
        if (proc is null) return;
        try { proc.Refresh(); } catch { return; }

        PidText = proc.Id.ToString();
        UptimeText = _core.UptimeText ?? "--";
        try
        {
            ThreadsText = proc.Threads.Count.ToString();
            HandlesText = proc.HandleCount.ToString();
        }
        catch { /* 个别平台/权限下可能不可读 */ }

        var now = DateTime.UtcNow;
        var cpuTime = proc.TotalProcessorTime;
        if (_lastSample != default && now - _lastSample > TimeSpan.FromMilliseconds(200))
        {
            var wall = (now - _lastSample).TotalSeconds;
            // 占整机百分比：进程 CPU 时间增量 / 墙钟增量 / 逻辑核数
            var pct = Math.Clamp(
                (cpuTime - _lastCpuTime).TotalSeconds / wall / Math.Max(1, Environment.ProcessorCount) * 100.0,
                0, 100);
            var memMb = proc.WorkingSet64 / 1048576.0;

            CpuText = $"{pct:F1}%";
            MemText = $"{memMb:F1} MB";
            AddPoint(_cpu, pct);
            AddPoint(_mem, memMb);
            OnPropertyChanged(nameof(CpuPoints));
            OnPropertyChanged(nameof(MemPoints));
        }
        _lastCpuTime = cpuTime;
        _lastSample = now;

        var ms = await _core.PingLatencyAsync();
        LatencyText = ms >= 0 ? $"{ms} ms" : Localizer.Get("status.unreachable");
    }

    private static void AddPoint(List<MetricPoint> list, double value)
    {
        list.Add(new MetricPoint(list.Count, value, DateTime.Now.ToString("HH:mm:ss")));
        if (list.Count > WindowSize) list.RemoveAt(0);
        // 重新编号，保持滚动窗口
        for (var i = 0; i < list.Count; i++)
            list[i] = list[i] with { Index = i };
    }
}
