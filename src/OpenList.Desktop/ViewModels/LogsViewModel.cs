using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Controls;
using OpenList.Desktop.Localization;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>日志表格里的一行（上游 LogView：时间/级别/来源/消息）。</summary>
public partial class LogRowViewModel : ObservableObject
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = LogLevels.Info;
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";

    public bool IsInfo => Level == LogLevels.Info;
    public bool IsWarn => Level == LogLevels.Warn;
    public bool IsError => Level == LogLevels.Error;
    public bool IsDebug => Level == LogLevels.Debug;

    public Avalonia.Media.IBrush LevelBackground => Level switch
    {
        LogLevels.Warn => BrushCache.Of("#33F1930F"),
        LogLevels.Error => BrushCache.Of("#33FF3B30"),
        LogLevels.Debug => BrushCache.Of("#1A86868B"),
        _ => BrushCache.Of("#1A007AFF"),
    };

    public Avalonia.Media.IBrush LevelForeground => Level switch
    {
        LogLevels.Warn => BrushCache.Of("#B378450C"),
        LogLevels.Error => BrushCache.Of("#B3CB2431"),
        LogLevels.Debug => BrushCache.Of("#FF86868B"),
        _ => BrushCache.Of("#FF007AFF"),
    };

    public string LevelText => Level switch
    {
        LogLevels.Warn => "WARN",
        LogLevels.Error => "ERROR",
        LogLevels.Debug => "DEBUG",
        _ => "INFO",
    };

    /// <summary>级别 pill 用的画刷缓存（只在 UI 线程访问）。</summary>
    private static class BrushCache
    {
        private static readonly Dictionary<string, Avalonia.Media.IBrush> Cache = [];

        public static Avalonia.Media.IBrush Of(string hex)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;
            var brush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex));
            Cache[hex] = brush;
            return brush;
        }
    }
}

/// <summary>日志页（上游 LogView 复刻）：工具条 + 级别/来源筛选 + 表格 + 导出 + 底部状态。</summary>
public partial class LogsViewModel : ViewModelBase
{
    /// <summary>内存中最多保留的日志条数，超出后丢弃最旧的（环形裁剪）。</summary>
    private const int MaxRows = 500;
    private readonly LogEventBus _bus;

    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _levelIndex; // 0 全部 1 info 2 warn 3 error
    [ObservableProperty] private int _sourceIndex; // 0 全部 1 OpenList 2 Rclone 3 桌面

    public ObservableCollection<LogRowViewModel> Rows { get; } = [];
    public ObservableCollection<LogRowViewModel> Filtered { get; } = [];

    /// <summary>
    /// 表格列定义。由 <see cref="BuildColumns"/> 在视图构造后注入（单元格模板是 XAML 资源，
    /// 只能在 Avalonia 起来后取）。列宽可拖拽、表头随语言切换刷新，细节见 <c>Controls/LogTable.cs</c>。
    /// </summary>
    public IReadOnlyList<LogColumn> TableColumns { get; private set; } = [];

    /// <summary>
    /// 用视图资源字典里的四个单元格模板组装列定义。
    /// 表头键走 <c>L.log.*</c>，语言切换时 DynamicResource 自动刷新。
    /// </summary>
    public void BuildColumns(Avalonia.Controls.IResourceDictionary resources)
    {
        TableColumns =
        [
            new LogColumn("L.log.colTime", Tpl(resources, "LogCellTime"), 110),
            // 级别是 pill，自身左右已有 Margin，这里不再叠加通用内边距
            new LogColumn("L.log.colLevel", Tpl(resources, "LogCellLevel"), 90, CellPadding: new Avalonia.Thickness(6, 0, 6, 0)),
            new LogColumn("L.log.colSource", Tpl(resources, "LogCellSource"), 100),
            new LogColumn("L.log.colMessage", Tpl(resources, "LogCellMessage"), 0, Star: 7, Resizable: false),
        ];
        OnPropertyChanged(nameof(TableColumns));
    }

    private static IDataTemplate Tpl(Avalonia.Controls.IResourceDictionary resources, string key) =>
        resources[key] as IDataTemplate
        ?? new FuncDataTemplate<object>((o, _) => new TextBlock { Text = o?.ToString() ?? "" });

    /// <summary>工具条上的计数（"已显示 / 总数"）。</summary>
    public string StatsText => $"{Filtered.Count} / {Rows.Count}";

    /// <summary>底部"显示 N 条记录"。</summary>
    public string RecordsText => Localizer.Format("log.stats", Filtered.Count);

    public string StatusText => string.Join("　　",
        Localizer.Format("log.statusAutoScroll", Localizer.Get(AutoScroll ? "common.on" : "common.off")),
        Localizer.Format("log.statusUpdates", Localizer.Get(Paused ? "log.updatesPaused" : "log.updatesLive")));

    public LogsViewModel(LogEventBus bus)
    {
        _bus = bus;
        foreach (var e in bus.Snapshot()) AppendRow(e);
        ApplyFilter();
        bus.OnEvent += e => Dispatcher.UIThread.Post(() =>
        {
            if (Paused) return;
            AppendRow(e);
        });
    }

    /// <summary>
    /// 定级：能明确判定的直接用事件自带级别；核心 stdout 只有纯文本（一律 INFO 进来），
    /// 再按关键字上调，保持与改造前一致的行为。
    /// 这里匹配的是<b>核心进程自己的输出</b>（中英混排），属于模式识别而非界面文案，故不做本地化。
    /// </summary>
    private static string DeriveLevel(LogEvent e)
    {
        if (e.Level != LogLevels.Info) return e.Level;

        var msg = e.Message;
        if (msg.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("error", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("失败") || msg.Contains("错误"))
            return LogLevels.Error;
        if (msg.Contains("warn", StringComparison.OrdinalIgnoreCase) || msg.Contains("警告"))
            return LogLevels.Warn;
        if (msg.Contains("debug", StringComparison.OrdinalIgnoreCase) || msg.Contains("调试"))
            return LogLevels.Debug;
        return LogLevels.Info;
    }

    private void AppendRow(LogEvent e)
    {
        Rows.Add(new LogRowViewModel
        {
            Time = e.Timestamp.LocalDateTime.ToString("HH:mm:ss"),
            Level = DeriveLevel(e),
            Source = e.Source.ToUpperInvariant(),
            Message = e.Message,
        });
        if (Rows.Count > MaxRows)
            Rows.RemoveAt(0);
        // 只对新增行做增量过滤（大多数场景新行都匹配）
        var row = Rows[^1];
        if (MatchesFilter(row))
        {
            Filtered.Add(row);
            if (Filtered.Count > MaxRows)
                Filtered.RemoveAt(0);
        }
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(RecordsText));
    }

    private bool MatchesFilter(LogRowViewModel r)
    {
        var kw = SearchText.Trim();
        if (kw.Length > 0 && !r.Message.Contains(kw, StringComparison.OrdinalIgnoreCase))
            return false;
        if (LevelIndex switch
            {
                1 => r.Level != LogLevels.Info,
                2 => r.Level != LogLevels.Warn,
                3 => r.Level != LogLevels.Error,
                _ => false,
            })
            return false;
        // Source 存的是语言无关的稳定标签（见 LogSources），因此筛选不受界面语言影响
        if (SourceIndex == 1 && r.Source is not (LogSources.CoreUpper)) return false;
        if (SourceIndex == 2 && r.Source is not (LogSources.RcloneUpper)) return false;
        if (SourceIndex == 3 && r.Source is not (LogSources.DesktopUpper)) return false;
        return true;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnLevelIndexChanged(int value) => ApplyFilter();
    partial void OnSourceIndexChanged(int value) => ApplyFilter();
    partial void OnPausedChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    partial void OnAutoScrollChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var r in Rows)
            if (MatchesFilter(r))
                Filtered.Add(r);
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(RecordsText));
    }

    [RelayCommand]
    private void TogglePause() => Paused = !Paused;

    [RelayCommand]
    private void Refresh() => ApplyFilter();

    [RelayCommand]
    private void Clear()
    {
        Rows.Clear();
        Filtered.Clear();
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(RecordsText));
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        var text = string.Join('\n', Filtered.Select(FormatLine));
        await UiServices.SetClipboardAsync(text);
        _bus.Info(LogSources.Desktop, Localizer.Format("log.copied", Filtered.Count));
    }

    /// <summary>
    /// 导出当前筛选结果。写 CSV（带 BOM，Excel 直接可读），
    /// 与"复制"一样只导出屏幕上看到的那批行。
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (UiServices.SaveTextFileAsync is null) return;
        if (Filtered.Count == 0)
        {
            _bus.Warn(LogSources.Desktop, Localizer.Get("log.nothingToExport"));
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("time,level,source,message");
        foreach (var r in Filtered)
            sb.AppendLine(string.Join(',',
                Csv(r.Time), Csv(r.Level), Csv(r.Source), Csv(r.Message)));

        var suggested = $"openlist-logs-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        try
        {
            var path = await UiServices.SaveTextFileAsync(suggested, sb.ToString());
            if (path is null) return;   // 用户取消
            _bus.Info(LogSources.Desktop, Localizer.Format("log.exported", Filtered.Count, path));
        }
        catch (Exception ex)
        {
            _bus.Error(LogSources.Desktop, Localizer.Format("log.exportFailed", ex.Message));
        }
    }

    private static string FormatLine(LogRowViewModel r) =>
        $"[{r.Time}] [{r.Level}] [{r.Source}] {r.Message}";

    /// <summary>CSV 字段转义：含逗号/引号/换行时加引号并双写内部引号。</summary>
    private static string Csv(string s)
    {
        if (s.IndexOfAny([',', '"', '\n', '\r']) < 0) return s;
        return '"' + s.Replace("\"", "\"\"") + '"';
    }
}
