using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>侧边导航项（Icon 为 lucide StreamGeometry）。</summary>
public sealed record NavItem(string Title, object Icon, ViewModelBase Page);

/// <summary>主窗口：上游同款窄侧栏（OpenList logo + 主题切换 + 导航 + GitHub 入口）。</summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppSettings _settingsStore;
    public DashboardViewModel Dashboard { get; }
    public MountsViewModel Mounts { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }
    public UpdateViewModel Update { get; }

    public ObservableCollection<NavItem> Pages { get; }

    [ObservableProperty] private NavItem? _selectedPage;

    [ObservableProperty] private bool _isDarkTheme;

    public MainWindowViewModel(
        DashboardViewModel dashboard, MountsViewModel mounts,
        LogsViewModel logs, SettingsViewModel settings, UpdateViewModel update,
        OpenListCoreService core, AppSettings settingsStore)
    {
        Dashboard = dashboard;
        Mounts = mounts;
        Logs = logs;
        Settings = settings;
        Update = update;
        _settingsStore = settingsStore;

        Pages =
        [
            new NavItem("主页", ResolveIcon("I.Home"), dashboard),
            new NavItem("挂载", ResolveIcon("I.HardDrive"), mounts),
            new NavItem("日志", ResolveIcon("I.FileText"), logs),
            new NavItem("设置", ResolveIcon("I.Settings"), settings),
            new NavItem("更新", ResolveIcon("I.Download"), update),
        ];
        _selectedPage = Pages[0];

        _isDarkTheme = settingsStore.Theme == "dark";

        // 页面跳转桥（核心管理卡等 VM 通过 UiServices.Navigate 切页）
        UiServices.Navigate = key =>
        {
            var page = key switch
            {
                "mount" or "mounts" => Pages.FirstOrDefault(p => p.Title == "挂载"),
                "settings" => Pages.FirstOrDefault(p => p.Title == "设置"),
                "logs" => Pages.FirstOrDefault(p => p.Title == "日志"),
                "home" or "dashboard" => Pages.FirstOrDefault(p => p.Title == "主页"),
                _ => null,
            };
            if (page is not null) SelectedPage = page;
        };
    }

    private static object ResolveIcon(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var value) is true && value is not null
            ? value
            : throw new InvalidOperationException($"图标资源缺失: {key}");

    /// <summary>退出收尾：停掉所有挂载；核心子进程由 CoreService.Dispose 收尾。</summary>
    public async Task OnClosingAsync()
    {
        foreach (var m in Mounts.Items.Where(m => m.IsRunning).ToList())
            await Mounts.UnmountCommand.ExecuteAsync(m);
    }

    /// <summary>上游 ThemeSwitcher：浅色 ↔ 深色循环切换。</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        var app = Application.Current;
        if (app is not null)
            app.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        _settingsStore.Theme = IsDarkTheme ? "dark" : "light";
        _settingsStore.Save();
    }
}
