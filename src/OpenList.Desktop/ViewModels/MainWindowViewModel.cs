using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Localization;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>
/// 侧边导航项。标题存 key 而非成品文案，语言切换时只需
/// <see cref="RefreshTitle"/> 即可让绑定重算，不必重建整页列表。
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(string titleKey, object icon, ViewModelBase page)
    {
        TitleKey = titleKey;
        Icon = icon;
        Page = page;
    }

    public string TitleKey { get; }

    /// <summary>Icon 为 lucide StreamGeometry。</summary>
    public object Icon { get; }

    public ViewModelBase Page { get; }

    public string Title => Localizer.Get(TitleKey);

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

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

    /// <summary>通知中心（浮层 toast 的数据源，见 MainWindow 右下角的 ItemsControl）。</summary>
    public NotificationService Notifications { get; }

    [ObservableProperty] private NavItem? _selectedPage;

    [ObservableProperty] private bool _isDarkTheme;

    /// <summary>主题切换按钮上的文案（"浅色" / "深色"），随语言切换刷新。</summary>
    public string ThemeToggleText => Localizer.Get(IsDarkTheme ? "nav.themeDark" : "nav.themeLight");

    public MainWindowViewModel(
        DashboardViewModel dashboard, MountsViewModel mounts,
        LogsViewModel logs, SettingsViewModel settings, UpdateViewModel update,
        AppSettings settingsStore, NotificationService notifications)
    {
        Dashboard = dashboard;
        Mounts = mounts;
        Logs = logs;
        Settings = settings;
        Update = update;
        _settingsStore = settingsStore;
        Notifications = notifications;

        Pages =
        [
            new NavItem("nav.home", ResolveIcon("I.Home"), dashboard),
            new NavItem("nav.mounts", ResolveIcon("I.HardDrive"), mounts),
            new NavItem("nav.logs", ResolveIcon("I.FileText"), logs),
            new NavItem("nav.settings", ResolveIcon("I.Settings"), settings),
            new NavItem("nav.update", ResolveIcon("I.Download"), update),
        ];
        _selectedPage = Pages[0];

        _isDarkTheme = settingsStore.Theme == "dark";

        // 页面跳转桥（核心管理卡等 VM 通过 UiServices.Navigate 切页）
        UiServices.Navigate = key =>
        {
            var index = key switch
            {
                "mount" or "mounts" => 1,
                "log" or "logs" => 2,
                "settings" => 3,
                "update" => 4,
                _ => 0,
            };
            SelectedPage = Pages[index];
        };

        // 托盘菜单是原生对象，语言切换时由 App 重建；这里只需刷新导航标题与主题按钮
        Localizer.Instance.LanguageChanged += () =>
        {
            foreach (var p in Pages) p.RefreshTitle();
            OnPropertyChanged(nameof(ThemeToggleText));
        };
    }

    private static object ResolveIcon(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var value) is true && value is not null
            ? value
            : throw new InvalidOperationException($"Icon resource missing: {key}");

    /// <summary>退出收尾：停掉所有挂载；核心子进程由 CoreService.Dispose 收尾。</summary>
    public async Task OnClosingAsync()
    {
        foreach (var m in Mounts.Items.Where(m => m.IsRunning).ToList())
            await Mounts.UnmountCommand.ExecuteAsync(m);
    }

    partial void OnIsDarkThemeChanged(bool value) => OnPropertyChanged(nameof(ThemeToggleText));

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
