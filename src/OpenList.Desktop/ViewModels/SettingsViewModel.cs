using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>设置页：核心路径/端口/数据目录、自启、rclone 路径。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly OpenListCoreService _core;
    private readonly LogEventBus _log;

    [ObservableProperty] private string _coreExePath = "";
    [ObservableProperty] private string _dataDir = "";
    [ObservableProperty] private int _httpPort = 5244;
    [ObservableProperty] private bool _autoStartCore = true;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _launchOnSystemStartup;
    [ObservableProperty] private bool _autoLogin = true;
    [ObservableProperty] private string _rclonePath = "rclone";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _settingsPathText;

    /// <summary>上游分段选择器：0=OpenList 服务 1=Rclone 存储 2=应用程序。</summary>
    [ObservableProperty] private int _selectedTabIndex;

    public bool Tab0Visible => SelectedTabIndex == 0;
    public bool Tab1Visible => SelectedTabIndex == 1;
    public bool Tab2Visible => SelectedTabIndex == 2;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Tab0Visible));
        OnPropertyChanged(nameof(Tab1Visible));
        OnPropertyChanged(nameof(Tab2Visible));
    }

    public bool AutostartOk => AutostartHelper.IsSupported;
    public string AutostartNote => AutostartHelper.IsSupported ? "" : "（当前平台不支持）";

    public string AutostartSupported => AutostartHelper.IsSupported ? "" : "（当前平台不支持）";

    public SettingsViewModel(AppSettings settings, OpenListCoreService core, LogEventBus log)
    {
        _settings = settings;
        _core = core;
        _log = log;
        _coreExePath = settings.CoreExePath;
        _dataDir = settings.DataDir;
        _httpPort = settings.HttpPort;
        _autoStartCore = settings.AutoStartCore;
        _startMinimized = settings.StartMinimized;
        _closeToTray = settings.CloseToTray;
        _launchOnSystemStartup = AutostartHelper.IsEnabled();
        _autoLogin = settings.AutoLogin;
        _rclonePath = settings.RclonePath;
        _settingsPathText = AppSettings.SettingsFilePath;
    }

    [RelayCommand]
    private async Task BrowseCoreAsync()
    {
        var storage = UiServices.MainWindow?.StorageProvider;
        if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 OpenList 核心可执行文件",
            AllowMultiple = false,
        });
        if (files.Count > 0)
            CoreExePath = files[0].Path.LocalPath;
    }

    [RelayCommand]
    private async Task BrowseDataDirAsync()
    {
        var storage = UiServices.MainWindow?.StorageProvider;
        if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择数据目录（数据库/日志存放位置）",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            DataDir = folders[0].Path.LocalPath;
    }

    [RelayCommand]
    private async Task BrowseRcloneAsync()
    {
        var storage = UiServices.MainWindow?.StorageProvider;
        if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 rclone 可执行文件",
            AllowMultiple = false,
        });
        if (files.Count > 0)
            RclonePath = files[0].Path.LocalPath;
    }

    [RelayCommand]
    private void Save()
    {        _settings.CoreExePath = CoreExePath.Trim();
        _settings.DataDir = DataDir.Trim();
        _settings.HttpPort = HttpPort > 0 ? HttpPort : 5244;
        _settings.AutoStartCore = AutoStartCore;
        _settings.StartMinimized = StartMinimized;
        _settings.CloseToTray = CloseToTray;
        _settings.AutoLogin = AutoLogin;
        _settings.RclonePath = string.IsNullOrWhiteSpace(RclonePath) ? "rclone" : RclonePath.Trim();
        _settings.Save();

        // 开机自启
        try
        {
            if (AutostartHelper.IsSupported)
            {
                var exe = Environment.ProcessPath;
                if (LaunchOnSystemStartup && exe is not null)
                    AutostartHelper.SetEnabled(true, exe);
                else
                    AutostartHelper.SetEnabled(false, "");
            }
            _settings.LaunchOnSystemStartup = LaunchOnSystemStartup;
            StatusText = "设置已保存";
            _log.Info("设置", "设置已保存（端口变更需重启核心生效）");
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetAll()
    {
        CoreExePath = _settings.CoreExePath;
        DataDir = _settings.DataDir;
        HttpPort = _settings.HttpPort;
        AutoStartCore = _settings.AutoStartCore;
        StartMinimized = _settings.StartMinimized;
        CloseToTray = _settings.CloseToTray;
        AutoLogin = _settings.AutoLogin;
        RclonePath = _settings.RclonePath;
        LaunchOnSystemStartup = AutostartHelper.IsEnabled();
        StatusText = "已重置为已保存的设置";
    }

    [RelayCommand]
    private void ResetCorePath()
    {
        CoreExePath = "";
        Save();
        var resolved = _core.ResolveCorePath();
        StatusText = resolved is null ? "自动探测失败：未找到核心" : $"已恢复自动探测: {resolved}";
    }
}
