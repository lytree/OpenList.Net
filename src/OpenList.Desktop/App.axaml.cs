using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Desktop.Services;
using OpenList.Desktop.ViewModels;
using OpenList.Desktop.Views;

namespace OpenList.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    /// <summary>托盘「退出」置位：跳过驻留托盘，真正结束程序。</summary>
    public static bool ForceExit { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        // 应用已保存的主题偏好（settings.Theme: light/dark），须在窗口创建前设置
        var settingsStore = Services.GetRequiredService<AppSettings>();
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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = window;

            var core = Services.GetRequiredService<OpenListCoreService>();

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

            // 自动拉起核心（对标上游"开机自启核心"）
            if (settingsStore.AutoStartCore)
                _ = core.StartAsync();
        }

        SetupTrayIcon();

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        var log = new LogEventBus();
        log.Info("桌面", "OpenList Desktop 启动");

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
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ViewModels.DashboardViewModel>();
        services.AddSingleton<ViewModels.CoreMonitorViewModel>();
        services.AddSingleton<ViewModels.MountsViewModel>();
        services.AddSingleton<ViewModels.LogsViewModel>();
        services.AddSingleton<ViewModels.SettingsViewModel>();
        services.AddSingleton<ViewModels.UpdateViewModel>();

        return services.BuildServiceProvider();
    }

    private void SetupTrayIcon()
    {
        var core = Services.GetRequiredService<OpenListCoreService>();
        var vm = Services.GetRequiredService<MainWindowViewModel>();

        var iconStream = AssetLoader.Open(new Uri("avares://OpenList.Desktop/Assets/app-icon.png"));
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("显示主窗口");
        showItem.Click += (_, _) =>
        {
            var win = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (win is not null)
            {
                win.Show();
                win.WindowState = WindowState.Normal;
                win.Activate();
            }
        };
        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());

        var startItem = new NativeMenuItem("启动核心");
        startItem.Click += (_, _) => _ = core.StartAsync();
        var stopItem = new NativeMenuItem("停止核心");
        stopItem.Click += (_, _) => _ = core.StopAsync();
        menu.Add(startItem);
        menu.Add(stopItem);
        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            ForceExit = true;
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };
        menu.Add(exitItem);

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "OpenList Desktop",
            Menu = menu,
            IsVisible = true,
        };
        tray.Clicked += (_, _) =>
        {
            var win = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (win is null) return;
            if (win.WindowState == WindowState.Minimized || !win.IsVisible)
            {
                win.Show();
                win.WindowState = WindowState.Normal;
                win.Activate();
            }
            else
            {
                win.WindowState = WindowState.Minimized;
            }
        };

        var icons = new TrayIcons();
        icons.Add(tray);
        TrayIcon.SetIcons(this, icons);
    }
}
