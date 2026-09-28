using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private readonly DispatcherTimer _timer;
    private int _tick;
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
        CoreMonitorViewModel monitor)
    {
        _core = core;
        _rclone = rclone;
        _settings = settings;
        _log = log;
        _updater = updater;
        Monitor = monitor;
        _isRunning = core.Status == CoreStatus.Running;
        core.StatusChanged += OnCoreStatusChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        _ = LoadVersionsAsync();
        FirewallEnabled = FirewallHelper.IsPortAllowed();
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
        if (++_tick % 3 != 0) return;

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

    /// <summary>复制管理员密码（核心 appsettings.json 的 AdminPassword）。</summary>
    [RelayCommand]
    private async Task CopyAdminPasswordAsync()
    {
        var pwd = ReadAdminPassword();
        if (string.IsNullOrEmpty(pwd))
        {
            _log.Error(LogSources.Dashboard, Localizer.Get("dash.passwordNotFound"));
            return;
        }
        await UiServices.SetClipboardAsync(pwd);
        _log.Info(LogSources.Dashboard, Localizer.Get("dash.passwordCopied"));
    }

    /// <summary>重置管理员密码（写回核心 appsettings.json，重启核心后生效）。</summary>
    [RelayCommand]
    private async Task ResetAdminPasswordAsync()
    {
        var path = FindCoreSettingsFile();
        if (path is null)
        {
            _log.Error(LogSources.Dashboard, Localizer.Get("dash.coreConfigMissing"));
            return;
        }
        var newPwd = Guid.NewGuid().ToString("N")[..12];
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var doc = JsonNode.Parse(json) as JsonObject;
            if (doc is null)
            {
                _log.Error(LogSources.Dashboard, Localizer.Get("dash.coreConfigInvalid"));
                return;
            }
            doc["AdminPassword"] = newPwd;
            await File.WriteAllTextAsync(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            await UiServices.SetClipboardAsync(newPwd);
            _log.Info(LogSources.Dashboard, Localizer.Get("dash.passwordReset"));
        }
        catch (Exception ex)
        {
            _log.Error(LogSources.Dashboard, Localizer.Format("dash.passwordResetFailed", ex.Message));
        }
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

    private static string? ReadAdminPassword()
    {
        try
        {
            var path = FindCoreSettingsFile();
            if (path is null) return null;
            var doc = JsonNode.Parse(File.ReadAllText(path));
            return doc?["AdminPassword"]?.ToString() ?? "admin";
        }
        catch { return null; }
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
