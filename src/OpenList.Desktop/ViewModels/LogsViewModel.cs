using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>日志表格里的一行（上游 LogView：时间/级别/来源/消息）。</summary>
public partial class LogRowViewModel : ObservableObject
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = "INFO";
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";

    public bool IsInfo => Level == "INFO";
    public bool IsWarn => Level == "WARN";
    public bool IsError => Level == "ERROR";
    public bool IsDebug => Level == "DEBUG";

    public Avalonia.Media.IBrush LevelBackground => Level switch
    {
        "WARN" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#33F1930F")),
        "ERROR" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#33FF3B30")),
        "DEBUG" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1A86868B")),
        _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1A007AFF")),
    };

    public Avalonia.Media.IBrush LevelForeground => Level switch
    {
        "WARN" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B378450C")),
        "ERROR" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B3CB2431")),
        "DEBUG" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF86868B")),
        _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF007AFF")),
    };

    public string LevelText => Level switch
    {
        "WARN" => "WARN",
        "ERROR" => "ERROR",
        "DEBUG" => "DEBUG",
        _ => "INFO",
    };
}

/// <summary>日志页（上游 LogView 复刻）：工具条 + 级别/来源筛选 + 表格 + 底部状态。</summary>
public partial class LogsViewModel : ViewModelBase
{
    private const int MaxRows = 2000;
    private readonly LogEventBus _bus;

    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _levelIndex; // 0 全部 1 info 2 warn 3 error
    [ObservableProperty] private int _sourceIndex; // 0 全部 1 OpenList 2 Rclone 3 桌面

    public ObservableCollection<LogRowViewModel> Rows { get; } = [];
    public ObservableCollection<LogRowViewModel> Filtered { get; } = [];

    public string StatsText => $"{Filtered.Count} / {Rows.Count}";
    public string StatusText => $"自动滚动: {(AutoScroll ? "开" : "关")}　更新: {(Paused ? "已暂停" : "实时")}";

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

    private static string DeriveLevel(LogEvent e)
    {
        var msg = e.Message;
        if (msg.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("error", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("失败") || msg.Contains("错误")
            || e.Source == "桌面错误")
            return "ERROR";
        if (msg.Contains("warn", StringComparison.OrdinalIgnoreCase) || msg.Contains("警告"))
            return "WARN";
        if (msg.Contains("debug", StringComparison.OrdinalIgnoreCase) || msg.Contains("调试"))
            return "DEBUG";
        return "INFO";
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
            OnPropertyChanged(nameof(StatsText));
        }
        OnPropertyChanged(nameof(StatsText));
    }

    private bool MatchesFilter(LogRowViewModel r)
    {
        var kw = SearchText.Trim();
        if (kw.Length > 0 && !r.Message.Contains(kw, StringComparison.OrdinalIgnoreCase))
            return false;
        if (LevelIndex switch { 1 => r.Level != "INFO", 2 => r.Level != "WARN", 3 => r.Level != "ERROR", _ => false })
            return false;
        if (SourceIndex == 1 && r.Source is not ("核心" or "CORE" or "OPENLIST")) return false;
        if (SourceIndex == 2 && !r.Source.Contains("RCLONE", StringComparison.OrdinalIgnoreCase)) return false;
        if (SourceIndex == 3 && r.Source is not ("桌面" or "DESKTOP")) return false;
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
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        var text = string.Join('\n', Filtered.Select(r => $"[{r.Time}] [{r.Level}] [{r.Source}] {r.Message}"));
        await UiServices.SetClipboardAsync(text);
    }
}
