using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Localization;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>设置页：核心路径/端口/数据目录、自启、rclone 路径、界面语言。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly OpenListCoreService _core;
    private readonly LogEventBus _log;
    private readonly OpenListApiClient _api;

    [ObservableProperty] private string _coreExePath = "";
    [ObservableProperty] private string _dataDir = "";
    [ObservableProperty] private int _httpPort = 5244;
    [ObservableProperty] private bool _autoStartCore = true;
    [ObservableProperty] private bool _autoRestartCore = true;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _launchOnSystemStartup;
    [ObservableProperty] private bool _autoLogin = true;
    [ObservableProperty] private string _rclonePath = "rclone";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _settingsPathText;
    [ObservableProperty] private LanguageOption? _selectedLanguage;

    // --- 内置管理员改密 ---
    [ObservableProperty] private string _adminPassword = "";
    [ObservableProperty] private string _adminPasswordNew = "";
    [ObservableProperty] private string _adminPasswordConfirm = "";
    [ObservableProperty] private bool _isChangingAdminPassword;

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

    /// <summary>语言下拉数据源。</summary>
    public IReadOnlyList<LanguageOption> Languages => Localizer.Languages;

    public bool AutostartOk => AutostartHelper.IsSupported;

    /// <summary>开机自启项的补充说明：支持时提示实现方式，不支持时明确告知。</summary>
    public string AutostartNote => Localizer.Get(
        AutostartHelper.IsSupported ? "set.launchOnStartupNote" : "set.notSupported");

    public SettingsViewModel(AppSettings settings, OpenListCoreService core, LogEventBus log, OpenListApiClient api)
    {
        _settings = settings;
        _core = core;
        _log = log;
        _api = api;
        _coreExePath = settings.CoreExePath;
        _dataDir = settings.DataDir;
        _httpPort = settings.HttpPort;
        _autoStartCore = settings.AutoStartCore;
        _autoRestartCore = settings.AutoRestartCore;
        _startMinimized = settings.StartMinimized;
        _closeToTray = settings.CloseToTray;
        _launchOnSystemStartup = AutostartHelper.IsEnabled();
        _autoLogin = settings.AutoLogin;
        _rclonePath = settings.RclonePath;
        _settingsPathText = AppSettings.SettingsFilePath;
        _selectedLanguage = Localizer.Languages.FirstOrDefault(l => l.Code == Localizer.Instance.Language)
                            ?? Localizer.Languages[0];
    }

    /// <summary>语言即时生效（不必先点保存），并持久化到设置文件。</summary>
    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value is null) return;
        if (!Localizer.Instance.Apply(value.Code)) return;
        _settings.Language = Localizer.Instance.Language;
        _settings.Save();
    }

    [RelayCommand]
    private async Task BrowseCoreAsync()
    {
        var storage = UiServices.MainWindow?.StorageProvider;
        if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("set.pickCore"),
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
            Title = Localizer.Get("set.pickDataDir"),
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
            Title = Localizer.Get("set.pickRclone"),
            AllowMultiple = false,
        });
        if (files.Count > 0)
            RclonePath = files[0].Path.LocalPath;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.CoreExePath = CoreExePath.Trim();
        _settings.DataDir = DataDir.Trim();
        _settings.HttpPort = HttpPort > 0 ? HttpPort : 5244;
        _settings.AutoStartCore = AutoStartCore;
        _settings.AutoRestartCore = AutoRestartCore;
        _settings.StartMinimized = StartMinimized;
        _settings.CloseToTray = CloseToTray;
        _settings.AutoLogin = AutoLogin;
        _settings.RclonePath = string.IsNullOrWhiteSpace(RclonePath) ? "rclone" : RclonePath.Trim();
        _settings.Language = Localizer.Instance.Language;
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
            StatusText = Localizer.Get("set.saved");
            _log.Info(LogSources.Settings, Localizer.Get("set.savedLog"));
        }
        catch (Exception ex)
        {
            StatusText = Localizer.Format("set.saveFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void ResetAll()
    {
        CoreExePath = _settings.CoreExePath;
        DataDir = _settings.DataDir;
        HttpPort = _settings.HttpPort;
        AutoStartCore = _settings.AutoStartCore;
        AutoRestartCore = _settings.AutoRestartCore;
        StartMinimized = _settings.StartMinimized;
        CloseToTray = _settings.CloseToTray;
        AutoLogin = _settings.AutoLogin;
        RclonePath = _settings.RclonePath;
        LaunchOnSystemStartup = AutostartHelper.IsEnabled();
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == _settings.Language) ?? Languages[0];
        StatusText = Localizer.Get("set.resetDone");
    }

    [RelayCommand]
    private void ResetCorePath()
    {
        CoreExePath = "";
        Save();
        var resolved = _core.ResolveCorePath();
        StatusText = resolved is null
            ? Localizer.Get("set.coreAutoFailed")
            : Localizer.Format("set.coreAutoOk", resolved);
    }

    /// <summary>
    /// 修改内置管理员密码。
    ///
    /// 流程是「用当前密码登录 → 换token → 调 reset_admin_password」：服务端把内置 admin
    /// 设为不可删除 / 不可改名 / 不可降权，所以它的密码就是唯一的恢复入口；这里让用户
    /// 在桌面端直接完成「验证旧密码 + 设置新密码」，不必先去网页端登录。
    ///
    /// 登录只为换取临时 token，成功后立即恢复原有登录态，避免顶掉用户当前的会话。
    /// </summary>
    [RelayCommand]
    private async Task ChangeAdminPasswordAsync()
    {
        if (IsChangingAdminPassword) return;

        if (string.IsNullOrWhiteSpace(AdminPasswordNew))
        {
            StatusText = Localizer.Get("set.adminPwdTooShort");
            return;
        }
        if (AdminPasswordNew.Length < 4)
        {
            StatusText = Localizer.Get("set.adminPwdTooShort");
            return;
        }
        if (!string.Equals(AdminPasswordNew, AdminPasswordConfirm, StringComparison.Ordinal))
        {
            StatusText = Localizer.Get("set.adminPwdMismatch");
            return;
        }
        if (_core.Status != CoreStatus.Running)
        {
            StatusText = Localizer.Get("set.adminPwdCoreDown");
            return;
        }

        IsChangingAdminPassword = true;
        // 记住原登录态：登录只为换 token，成功后要还原。
        var previousToken = _settings.AuthToken;
        try
        {
            // 复用桌面端已保存的 token（若用户此前在网页端登录过）则无需重新验证旧密码；
            // 没有 token 时才要求输入当前密码来换一个。
            if (string.IsNullOrEmpty(previousToken))
            {
                if (string.IsNullOrWhiteSpace(AdminPassword))
                {
                    StatusText = Localizer.Get("set.adminPwdNeedLogin");
                    return;
                }
                _api.ServerUrl = _settings.ServerUrl;
                await _api.LoginAsync("admin", AdminPassword, null);
            }
            else
            {
                _api.SetToken(previousToken);
            }

            await _api.ResetAdminPasswordAsync(AdminPasswordNew);

            StatusText = Localizer.Get("set.adminPwdDone");
            _log.Info(LogSources.Settings, Localizer.Get("set.adminPwdDone"));
            // 清空输入框，避免密码留在内存/界面上。
            AdminPassword = "";
            AdminPasswordNew = "";
            AdminPasswordConfirm = "";
        }
        catch (Exception ex)
        {
            // ApiException 的 message 已是服务端返回的可读文案（如
            // "password must be at least 4 characters"），直接展示即可。
            StatusText = Localizer.Format("set.adminPwdFailed", ex.Message);
            _log.Error(LogSources.Settings, Localizer.Format("set.adminPwdFailed", ex.Message));
        }
        finally
        {
            // 无论成败都还原登录态：临时登录只用于换 token。
            _api.SetToken(previousToken);
            IsChangingAdminPassword = false;
        }
    }
}
