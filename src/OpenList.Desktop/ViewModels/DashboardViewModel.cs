using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Localization;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>心跳图上的一个采样点（X/Y 由视图按画布大小归一化前给定：序号 + 延迟ms）。</summary>
public sealed record HeartbeatPoint(int Index, long Ms, bool Healthy, string Time);

/// <summary>版本下拉条目。</summary>
public sealed record VersionEntry(string Tag, string Url)
{
    public override string ToString() => Tag;
}

/// <summary>
/// 主页（上游 Dashboard 复刻）：核心管理、核心监控（心跳图）、版本管理、文档卡。
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly OpenListCoreService _core;
    private readonly RcloneService _rclone;
    private readonly AppSettings _settings;
    private readonly LogEventBus _log;
    private readonly UpdateChecker _updater;
    private readonly OpenListApiClient _api;
    private readonly DispatcherTimer _timer;
    private readonly List<HeartbeatPoint> _points = [];

    /// <summary>rclone 版本原始值；为空表示不可用，显示时再按语言渲染。</summary>
    private string _rcloneVersion = "";

    // ---- 核心管理 ----
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;

    // ---- 核心监控 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseTimeLabel))]
    private string _responseTimeText = "--";
    [ObservableProperty] private string _avgResponseTimeText = "--";
    [ObservableProperty] private string _uptimeRateText = "--";
    [ObservableProperty] private bool _hasHeartbeat;

    // ---- 防火墙 ----
    [ObservableProperty] private bool _firewallEnabled;
    [ObservableProperty] private bool _firewallBusy;

    // ---- 版本管理 ----
    // OpenList 核心随桌面端一起发布，不单独管理版本；仅 Rclone 需要选版本/更新。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoreVersion))]
    private string _coreVersionText = "";

    [ObservableProperty] private VersionEntry? _selectedRcloneVersion;

    public bool HasCoreVersion => !string.IsNullOrEmpty(CoreVersionText);

    /// <summary>rclone 版本或"未安装"（按当前语言渲染）。</summary>
    public string RcloneVersionText => string.IsNullOrEmpty(_rcloneVersion)
        ? Localizer.Get("dash.rcloneNotInstalled")
        : _rcloneVersion;

    public bool HasRcloneVersion => !string.IsNullOrEmpty(_rcloneVersion);

    public ObservableCollection<VersionEntry> RcloneVersions { get; } = [];

    /// <summary>心跳采样点（视图监听 PropertyChanged(nameof(HeartbeatPoints)) 重绘）。</summary>
    public IReadOnlyList<HeartbeatPoint> HeartbeatPoints => _points;

    public string PortText => _settings.HttpPort.ToString();

    /// <summary>带标签的端口文案（视图直接绑定，避免 XAML 里拼 StringFormat 无法本地化）。</summary>
    public string PortLabel => Localizer.Format("dash.port", _settings.HttpPort);

    /// <summary>带标签的响应时间文案。</summary>
    public string ResponseTimeLabel => Localizer.Format("dash.responseTime", ResponseTimeText);

    /// <summary>进程监控（核心子进程 CPU/内存/线程/句柄实时采集）。</summary>
    public CoreMonitorViewModel Monitor { get; }

    public DashboardViewModel(OpenListCoreService core, RcloneService rclone,
        AppSettings settings, LogEventBus log, UpdateChecker updater,
        CoreMonitorViewModel monitor, OpenListApiClient api)
    {
        _core = core;
        _rclone = rclone;
        _settings = settings;
        _log = log;
        _updater = updater;
        _api = api;
        Monitor = monitor;
        _isRunning = core.Status == CoreStatus.Running;
        core.StatusChanged += OnCoreStatusChanged;

        _timer = new DispatcherTimer { Interval = settings.HeartbeatInterval };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        // 设置页改心跳间隔后立即生效，不必重启桌面端
        settings.HeartbeatIntervalChanged += () => _timer.Interval = settings.HeartbeatInterval;

        _ = LoadVersionsAsync();
        FirewallEnabled = FirewallHelper.IsPortAllowed();
    }

    /// <summary>
    /// 页面可见性开关。主页不在前台时停止心跳探测 —— 采样只为画曲线，用户看不到时
    /// 每秒打一次 <c>/ping</c> 是纯开销（还会唤起服务端的连接与日志）。
    /// 由 <see cref="MainWindowViewModel"/> 在切换导航页时驱动。
    /// </summary>
    /// <param name="visible">主页是否处于前台。</param>
    public void SetVisible(bool visible)
    {
        if (visible) _timer.Start();
        else _timer.Stop();
    }

    private void OnCoreStatusChanged(CoreStatus s)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsRunning = s == CoreStatus.Running;
            if (s == CoreStatus.Running)
            {
                _rclone.AutoMountAll();
                if (_settings.AutoLogin)
                    _settings.Save();
            }
            else
            {
                _points.Clear();
                RefreshHeartbeat();
            }
        });
    }

    private async Task TickAsync()
    {
        if (_core.Status != CoreStatus.Running)
        {
            ResponseTimeText = "--";
            return;
        }

        var ms = await _core.PingLatencyAsync();
        var healthy = ms >= 0;
        ResponseTimeText = healthy ? $"{ms} ms" : Localizer.Get("status.unreachable");
        _points.Add(new HeartbeatPoint(_points.Count, healthy ? ms : 0, healthy,
            DateTime.Now.ToString("HH:mm:ss")));
        if (_points.Count > 60)
            _points.RemoveAt(0);
        // 重新编号，滚动窗口
        for (var i = 0; i < _points.Count; i++)
            _points[i] = _points[i] with { Index = i };

        var recent = _points.TakeLast(20).ToList();
        var avg = (long)recent.Where(p => p.Healthy).Select(p => p.Ms).DefaultIfEmpty(0).Average();
        AvgResponseTimeText = recent.Any(p => p.Healthy)
            ? Localizer.Format("dash.avgLatency", avg)
            : "--";
        var okRate = recent.Count(p => p.Healthy) * 100.0 / recent.Count;
        UptimeRateText = Localizer.Format("dash.uptimeRate", okRate.ToString("F0"));
        RefreshHeartbeat();
    }

    private void RefreshHeartbeat() => OnPropertyChanged(nameof(HeartbeatPoints));

    // ---------------- 核心管理 ----------------

    [RelayCommand]
    private async Task ToggleCoreAsync()
    {
        IsBusy = true;
        try
        {
            if (IsRunning)
            {
                await _rclone.StopAllAsync();
                await _core.StopAsync();
            }
            else
            {
                await _core.StartAsync();
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenWeb()
    {
        if (!IsRunning) return;
        UpdateChecker.OpenUrl(_core.BaseUrl);
    }

    /// <summary>
    /// 复制管理员密码。
    ///
    /// 密码在数据库里只存哈希，配置里的 AdminPassword 仅在首次建库时被读取一次，因此这里
    /// 复制的是<em>建库时用的初始密码</em>；用户若已在管理界面或设置页改过密码，它就不再
    /// 有效 —— 那种情况下提示用设置页的改密功能，而非给出错误的凭据。
    /// </summary>
    [RelayCommand]
    private async Task CopyAdminPasswordAsync()
    {
        var pwd = ReadSeedAdminPassword();
        if (string.IsNullOrEmpty(pwd))
        {
            _log.Error(LogSources.Dashboard, Localizer.Get("dash.passwordNotFound"));
            return;
        }
        await UiServices.SetClipboardAsync(pwd);
        _log.Info(LogSources.Dashboard, Localizer.Get("dash.passwordCopied"));
    }

    /// <summary>
    /// 重置内置管理员密码并复制到剪贴板。
    ///
    /// 改为直接调用服务端 <c>POST /api/admin/user/reset_admin_password</c>：旧实现是
    /// 改写核心 appsettings.json 的 AdminPassword 再等核心重启生效，但初始化逻辑已改为
    /// 「仅建库时读一次配置、之后完全由数据库为准」，那条路径不再有效。
    ///
    /// 内置 admin 被刻意设为不可删除 / 不可改名 / 不可降权，所以这个接口（配合设置页的
    /// 手动改密）是忘记密码后唯一的恢复入口 —— 因此不要求旧密码，但要能以 admin 身份
    /// 调通：已保存 token 时直接用，否则用配置里的初始密码登录一次。
    /// </summary>
    [RelayCommand]
    private async Task ResetAdminPasswordAsync()
    {
        if (_core.Status != CoreStatus.Running)
        {
            _log.Error(LogSources.Dashboard, Localizer.Get("set.adminPwdCoreDown"));
            return;
        }

        var previousToken = _api.Token;
        try
        {
            _api.ServerUrl = _settings.ServerUrl;
            if (string.IsNullOrEmpty(previousToken))
            {
                // 没有可用会话：用配置里的初始密码换一个临时 token。
                var seed = ReadSeedAdminPassword() ?? "admin";
                await _api.LoginAsync("admin", seed, null);
            }
            else
            {
                _api.SetToken(previousToken);
            }

            var newPwd = Guid.NewGuid().ToString("N")[..12];
            await _api.ResetAdminPasswordAsync(newPwd);
            await UiServices.SetClipboardAsync(newPwd);
            _log.Info(LogSources.Dashboard, Localizer.Get("dash.passwordReset"));
        }
        catch (Exception ex)
        {
            _log.Error(LogSources.Dashboard, Localizer.Format("dash.passwordResetFailed", ex.Message));
        }
        finally
        {
            // 临时登录只用于换取调用权限，结束后恢复原会话。
            _api.SetToken(previousToken);
        }
    }

    /// <summary>读取核心 appsettings.json 里的初始 AdminPassword（仅建库时使用过一次）。</summary>
    private static string? ReadSeedAdminPassword()
    {
        try
        {
            var path = FindCoreSettingsFile();
            if (path is null) return null;
            var doc = JsonNode.Parse(File.ReadAllText(path));
            return doc?["AdminPassword"]?.ToString();
        }
        catch { return null; }
    }

    private static string? FindCoreSettingsFile()
    {
        // 桌面 exe 与核心 exe 常在同目录；开发模式下在 Server 输出目录
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "OpenList.Server", "bin", "Debug", "net10.0", "appsettings.json")),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------------- 防火墙 ----------------

    [RelayCommand]
    private void ToggleFirewall()
    {
        if (FirewallBusy || !FirewallHelper.IsSupported()) return;
        FirewallBusy = true;
        try
        {
            if (FirewallEnabled)
            {
                FirewallEnabled = !FirewallHelper.RemoveRule();
                _log.Info(LogSources.Dashboard, Localizer.Get(
                    FirewallEnabled ? "dash.firewallRemoveFailed" : "dash.firewallRemoved"));
            }
            else
            {
                FirewallEnabled = FirewallHelper.AllowPort(_settings.HttpPort);
                _log.Info(LogSources.Dashboard, Localizer.Get(
                    FirewallEnabled ? "dash.firewallAllowed" : "dash.firewallAllowFailed"));
            }
        }
        finally { FirewallBusy = false; }
    }

    // ---------------- 版本管理 ----------------

    private async Task LoadVersionsAsync()
    {
        CoreVersionText = GetCoreVersion();
        _rcloneVersion = await GetRcloneVersionAsync();
        OnPropertyChanged(nameof(RcloneVersionText));
        OnPropertyChanged(nameof(HasRcloneVersion));

        var rcloneReleases = await _updater.ListReleasesAsync("rclone/rclone");
        RcloneVersions.Clear();
        foreach (var r in rcloneReleases) RcloneVersions.Add(new VersionEntry(r.Tag, r.Url));
    }

    private static string GetCoreVersion()
    {
        try
        {
            // 桌面端与核心同进程树发布，读自身 / 核心的程序集版本
            var exe = Path.Combine(AppContext.BaseDirectory, "OpenList.Server.exe");
            var target = File.Exists(exe) ? exe : AppContext.BaseDirectory;
            var vi = FileVersionInfo.GetVersionInfo(target);
            return vi.FileVersion is { } v ? $"v{v}" : "";
        }
        catch { return ""; }
    }

    /// <summary>取 rclone 版本首行；不可用时返回空串（显示层渲染为"未安装"）。</summary>
    private async Task<string> GetRcloneVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _settings.RclonePath,
                Arguments = "version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var first = await p.StandardOutput.ReadLineAsync();
            await p.WaitForExitAsync(new CancellationTokenSource(8000).Token);
            return first?.Trim() ?? "";
        }
        catch { return ""; }
    }

    [RelayCommand]
    private void UpdateRclone() => OpenSelected(SelectedRcloneVersion);

    private static void OpenSelected(VersionEntry? entry)
    {
        if (entry is not null) UpdateChecker.OpenUrl(entry.Url);
    }

    // ---------------- 文档链接 ----------------

    /// <summary>上游 QuickActionsCard：配置 RClone → 设置页。</summary>
    [RelayCommand]
    private void OpenRcloneConfig() => UiServices.Navigate?.Invoke("settings");

    /// <summary>上游 QuickActionsCard：管理挂载 → 挂载页。</summary>
    [RelayCommand]
    private void ManageMounts() => UiServices.Navigate?.Invoke("mount");

    [RelayCommand] private void OpenDocs() => UpdateChecker.OpenUrl("https://doc.oplist.org/");
    [RelayCommand] private void OpenOpenListGithub() => UpdateChecker.OpenUrl("https://github.com/OpenListTeam/OpenList");
    [RelayCommand] private void OpenRcloneDocs() => UpdateChecker.OpenUrl("https://rclone.org/docs/");
    [RelayCommand] private void OpenRcloneGithub() => UpdateChecker.OpenUrl("https://github.com/rclone/rclone");
    [RelayCommand] private void OpenApiDocs() { if (IsRunning) UpdateChecker.OpenUrl($"{_core.BaseUrl}/swagger"); else UpdateChecker.OpenUrl("https://doc.oplist.org/guide/api"); }
    [RelayCommand] private void OpenMountCommands() => UpdateChecker.OpenUrl("https://rclone.org/commands/rclone_mount/");
    [RelayCommand] private void OpenIssues() => UpdateChecker.OpenUrl($"{AppInfo.RepoUrl}/issues");
    [RelayCommand] private void OpenFaq() => UpdateChecker.OpenUrl("https://doc.oplist.org/faq");
}
