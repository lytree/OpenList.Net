using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Desktop.Localization;
using OpenList.Desktop.Services;
using OpenList.Desktop.ViewModels;
using OpenList.Desktop.Views;

namespace OpenList.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    /// <summary>托盘图标（由 <see cref="SetupTrayIcon"/> 建立；通知中心借此更新悬停提示）。</summary>
    public static TrayIcon? Tray { get; private set; }

    /// <summary>托盘「退出」置位：跳过驻留托盘，真正结束程序。</summary>
    public static bool ForceExit { get; set; }

    private TrayIcons? _trayIcons;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        var settingsStore = Services.GetRequiredService<AppSettings>();
        var core = Services.GetRequiredService<OpenListCoreService>();
        var notify = Services.GetRequiredService<NotificationService>();

        // 本地化必须发生在任何窗口/托盘创建之前：DynamicResource 只在整个应用生命周期内
        // 反复解析，但资源词典得先在 Application.Resources 里才能被找到。
        Localizer.Instance.Apply(settingsStore.Language);
        Localizer.AttachTo(this);
        Localizer.Instance.LanguageChanged += OnLanguageChanged;

        // 应用已保存的主题偏好（settings.Theme: light/dark），须在窗口创建前设置
        RequestedThemeVariant = settingsStore.Theme == "dark"
            ? global::Avalonia.Styling.ThemeVariant.Dark
            : global::Avalonia.Styling.ThemeVariant.Light;

        var vm = Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };

        // UI 桥
        UiServices.MainWindow = window;
        UiServices.InputAsync = (arg, def) =>
        {
            var parts = arg.Split('|', 2);
            return InputDialog.ShowAsync(window, parts[0], parts.Length > 1 ? parts[1] : "", def);
        };
        UiServices.SaveTextFileAsync = UiServices.SaveTextFileWithPickerAsync;

        // 核心状态 → 托盘提示 + 浮层通知。
        // 必须在下方的 StartAsync 之前接好，否则"核心已就绪"这条通知会因为在订阅前发出而丢失。
        notify.SetCoreStatus(core.Status);
        core.StatusChanged += s => notify.SetCoreStatus(s);
        core.Notice += n => notify.Notify(
            n.TitleKey, n.BodyKey,
            n.IsError ? NoticeLevel.Warning : NoticeLevel.Success,
            n.BodyArgs);

        SetupTrayIcon();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = window;

            // 上游 OpenList-Desktop 同款行为：设置开启"启动最小化"则只进托盘
            if (settingsStore.StartMinimized)
                window.WindowState = WindowState.Minimized;
            else
                window.Show();

            // 退出时收尾：停挂载、杀核心子进程，避免孤儿进程
            desktop.ShutdownRequested += (_, _) =>
            {
                _ = vm.OnClosingAsync();
                core.Dispose();
            };

            // 自动拉起核心（对标上游"开机自启核心"）；通知已接线，启动结果会提示到浮层
            if (settingsStore.AutoStartCore)
                _ = core.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>语言切换：托盘菜单文案是原生对象，得整体重建（必须在 UI 线程）。</summary>
    private void OnLanguageChanged()
    {
        void Rebuild()
        {
            SetupTrayIcon();
            var core = Services.GetService<OpenListCoreService>();
            if (core is not null)
                Services.GetService<NotificationService>()?.SetCoreStatus(core.Status);
        }

        if (Dispatcher.UIThread.CheckAccess()) Rebuild();
        else Dispatcher.UIThread.Post(Rebuild);
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        var log = new LogEventBus();
        log.Info(LogSources.Desktop, "OpenList Desktop started");

        var settings = AppSettings.Load();
        var core = new OpenListCoreService(settings, log);
        var api = new OpenListApiClient { ServerUrl = settings.ServerUrl };
        if (!string.IsNullOrEmpty(settings.AuthToken))
            api.SetToken(settings.AuthToken);
        var rclone = new RcloneService(settings, log);
        var update = new UpdateChecker(log);

        services.AddSingleton(log);
        services.AddSingleton(settings);
        services.AddSingleton(core);
        services.AddSingleton(api);
        services.AddSingleton(rclone);
        services.AddSingleton(update);
        services.AddSingleton<NotificationService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ViewModels.DashboardViewModel>();
        services.AddSingleton<ViewModels.CoreMonitorViewModel>();
        services.AddSingleton<ViewModels.MountsViewModel>();
        services.AddSingleton<ViewModels.LogsViewModel>();
        services.AddSingleton<ViewModels.SettingsViewModel>();
        services.AddSingleton<ViewModels.UpdateViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 建立（或按当前语言重建）托盘图标与菜单。
    /// 菜单文案是 <see cref="NativeMenuItem.Text"/> 这类原生字符串，不会跟随
    /// DynamicResource 刷新，因此语言切换时直接整组替换。
    /// </summary>
    private void SetupTrayIcon()
    {
        var core = Services.GetRequiredService<OpenListCoreService>();

        var iconStream = AssetLoader.Open(new Uri("avares://OpenList.Desktop/Assets/app-icon.png"));
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem(Localizer.Get("tray.show"));
        showItem.Click += (_, _) => RestoreWindow();
        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());

        var startItem = new NativeMenuItem(Localizer.Get("tray.startCore"));
        startItem.Click += (_, _) => _ = core.StartAsync();
        var stopItem = new NativeMenuItem(Localizer.Get("tray.stopCore"));
        stopItem.Click += (_, _) => _ = core.StopAsync();
        menu.Add(startItem);
        menu.Add(stopItem);
        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem(Localizer.Get("tray.exit"));
        exitItem.Click += (_, _) =>
        {
            ForceExit = true;
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };
        menu.Add(exitItem);

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = Localizer.Format("tray.tooltip",
                Localizer.Get(NotificationService.CoreStatusKey(core.Status))),
            Menu = menu,
            IsVisible = true,
        };
        tray.Clicked += (_, _) =>
        {
            var win = MainWindow;
            if (win is null) return;
            if (win.WindowState == WindowState.Minimized || !win.IsVisible) RestoreWindow();
            else win.WindowState = WindowState.Minimized;
        };

        _trayIcons ??= new TrayIcons();
        _trayIcons.Clear();
        _trayIcons.Add(tray);
        TrayIcon.SetIcons(this, _trayIcons);
        Tray = tray;
    }

    private Window? MainWindow =>
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private void RestoreWindow()
    {
        var win = MainWindow;
        if (win is null) return;
        win.Show();
        win.WindowState = WindowState.Normal;
        win.Activate();
    }
}
