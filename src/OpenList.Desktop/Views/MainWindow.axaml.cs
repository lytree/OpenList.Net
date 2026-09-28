using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _closingHandled;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnGitHubClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        UpdateChecker.OpenUrl(AppInfo.RepoUrl);

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // 托盘运行：普通关窗 → 隐藏到托盘继续运行（挂载、核心子进程都不中断）；
        // 托盘「退出」或设置关闭驻留时，走清理流程真正关闭。
        var settings = App.Services?.GetService<AppSettings>();
        if (!_closingHandled && settings is { CloseToTray: true } && !App.ForceExit)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                App.Services?.GetService<NotificationService>()
                    ?.Notify("tray.residentTitle", "tray.resident", NoticeLevel.Info);
            }
            return;
        }

        if (DataContext is ViewModels.MainWindowViewModel vm && !_closingHandled)
        {
            // 先清理（卸载挂载、随后核心子进程由服务 Dispose 收尾），再真正关闭
            e.Cancel = true;
            _closingHandled = true;
            await vm.OnClosingAsync();
            Close();
        }
    }
}
