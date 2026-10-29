using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    private readonly NotificationService _notify;
    private readonly DispatcherTimer _timer;
    private readonly List<HeartbeatPoint> _points = [];

    /// <summary>rclone 版本原始值；为空表示不可用，显示时再按语言渲染。</summary>
    private string _rcloneVersion = "";

    /// <summary>本机记住的明文密码（空 = 不知道）。见 <see cref="AppSettings.AdminPassword"/>。</summary>
    private string _credentialPassword = "";

    /// <summary>本次进程内是否已完成「检查系统是否初始化」这一步。</summary>
    private bool _firstRunDone;

    /// <summary>检查/建号是否正在进行（状态事件与构造函数可能同时触发，避免建两次）。</summary>
    private bool _firstRunBusy;

    // ---- 核心管理 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsHint))]
    private bool _isRunning;
    [ObservableProperty] private bool _isBusy;

    // ---- 登录凭据（首次自动初始化 / 密码展示） ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsHint))]
    private bool _hasAdminCredentials;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdminPasswordText))]
    [NotifyPropertyChangedFor(nameof(AdminPasswordToggleText))]
    private bool _showAdminPassword;
    [ObservableProperty] private string _adminUsernameText = "admin";

    /// <summary>密码展示文本：默认掩码，点「显示」才出明文；本机没记录时直接说明。</summary>
    public string AdminPasswordText =>
        _credentialPassword.Length == 0
            ? Localizer.Get("dash.credNoPassword")
            : ShowAdminPassword
                ? _credentialPassword
                : new string('•', Math.Clamp(_credentialPassword.Length, 6, 12));

    /// <summary>「显示 / 隐藏」按钮文案。</summary>
    public string AdminPasswordToggleText => Localizer.Get(ShowAdminPassword ? "dash.credHide" : "dash.credShow");

    /// <summary>凭据卡片下方的说明，按「核心是否在跑 / 本机是否记得密码」换措辞。</summary>
    public string CredentialsHint =>
        !IsRunning ? Localizer.Get("dash.credCoreDown")
        : HasAdminCredentials ? Localizer.Get("dash.credHintKnown")
        : Localizer.Get("dash.credHintUnknown");

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
        CoreMonitorViewModel monitor, OpenListApiClient api, NotificationService notify)
    {
        _core = core;
        _rclone = rclone;
        _settings = settings;
        _log = log;
        _updater = updater;
        _api = api;
        _notify = notify;
        Monitor = monitor;
        _isRunning = core.Status == CoreStatus.Running;
        core.StatusChanged += OnCoreStatusChanged;

        // 核心建号时会把初始密码打进 stdout，顺手解析出来显示（开发态 admin/admin、
        // 或用户自己配了 OpenList__AdminPassword 这两种情况都不经过下面的自动建号）。
        _log.OnEvent += OnLogEvent;

        RefreshCredentials();

        _timer = new DispatcherTimer { Interval = settings.HeartbeatInterval };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        // 设置页改心跳间隔后立即生效，不必重启桌面端
        settings.HeartbeatIntervalChanged += () => _timer.Interval = settings.HeartbeatInterval;

        _ = LoadVersionsAsync();
        FirewallEnabled = FirewallHelper.IsPortAllowed();
        // 核心先在跑（比如窗口重建）时不会再有状态变更事件，这里补一次检查
        if (_isRunning) _ = EnsureFirstRunAsync();
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
                // 核心就绪 = 可以确认系统有没有管理员：没有就自动建号并把密码交到用户手上
                _ = EnsureFirstRunAsync();
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

    // ---------------- 首次初始化 / 登录凭据 ----------------

    /// <summary>
    /// 核心就绪后确保系统有一个可用的管理员，并把密码交到用户手上。
    ///
    /// <para>
    /// 为什么这件事必须由桌面端做：核心在没配置 <c>AdminPassword</c> 时是<b>故意不建</b>
    /// 管理员的（等 Web 安装向导来建，见 <c>DbInitializer.SeedAdminAsync</c>），而桌面端把核心
    /// 当子进程拉起、GUI 又没有控制台 —— 于是「一个账号都没有 + 谁也看不到密码」成了死胡同。
    /// 旧的分发包更糟：里面那个前端是上游 OpenList-Frontend，压根没有安装向导页面，
    /// 打开就是登录页。
    /// </para>
    /// <para>
    /// 所以这里在核心就绪后问一次 <c>/api/public/init_status</c>：还没初始化就<b>在本机生成</b>
    /// 一个随机密码建号（服务端只存哈希，明文只在本机出现这一次），复制到剪贴板并在主页
    /// 「登录凭据」卡片里长期展示。
    /// </para>
    /// <para>
    /// 只对<b>本机</b>核心这么做：<see cref="AppSettings.ServerUrl"/> 指向远端实例时，
    /// 替别人的服务器建管理员账号不是我们该做的事。
    /// </para>
    /// </summary>
    private async Task EnsureFirstRunAsync()
    {
        if (_firstRunDone || _firstRunBusy) return;
        if (!Uri.TryCreate(_settings.ServerUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback) return;

        _firstRunBusy = true;
        _api.ServerUrl = _settings.ServerUrl;
        try
        {
            if (await _api.IsInitializedAsync())
            {
                _firstRunDone = true;
                RefreshCredentials();
                await TryAutoLoginAsync();
                return;
            }

            var username = string.IsNullOrWhiteSpace(_settings.AdminUsername) ? "admin" : _settings.AdminUsername.Trim();
            _log.Info(LogSources.Dashboard, Localizer.Get("dash.setupCreating"));

            var password = GeneratePassword();
            await _api.CreateInitialAdminAsync(username, password);

            RememberCredentials(username, password);
            _firstRunDone = true;

            await UiServices.SetClipboardAsync(password);
            _log.Info(LogSources.Dashboard, Localizer.Format("dash.setupCreated", username, password));
            _notify.Notify("dash.setupCreatedTitle", "dash.setupCreatedBody",
                NoticeLevel.Success, username, password);

            await TryAutoLoginAsync();
        }
        catch (Exception ex)
        {
            // 不置 _firstRunDone：核心重启 / 暂时不可达时，下次就绪还能再试一遍。
            _log.Error(LogSources.Dashboard, Localizer.Format("dash.setupFailed", ex.Message));
        }
        finally
        {
            _firstRunBusy = false;
        }
    }

    /// <summary>用本机记住的凭据登录一次（换取 token，供重置密码等管理接口使用）。</summary>
    private async Task TryAutoLoginAsync()
    {
        if (!_settings.AutoLogin || _api.IsLoggedIn) return;
        if (string.IsNullOrEmpty(_settings.AdminPassword)) return;

        try
        {
            _api.ServerUrl = _settings.ServerUrl;
            await _api.LoginAsync(AdminUsernameText, _settings.AdminPassword, null);
        }
        catch (Exception ex)
        {
            // 密码可能已在网页里改过 —— 登录失败不影响主页，只留下一条可查的日志。
            // 刻意不持久化 token：过期的 token 会让「重置密码」误以为自己已登录。
            _log.Warn(LogSources.Dashboard, Localizer.Format("dash.setupLoginFailed", ex.Message));
        }
    }

    private void RememberCredentials(string username, string password)
    {
        _settings.AdminUsername = username;
        _settings.AdminPassword = password;
        _settings.Save();
        RefreshCredentials();
    }

    /// <summary>把设置里的凭据同步到界面（用户名、掩码、说明文字）。</summary>
    private void RefreshCredentials()
    {
        _credentialPassword = _settings.AdminPassword ?? "";
        AdminUsernameText = string.IsNullOrWhiteSpace(_settings.AdminUsername) ? "admin" : _settings.AdminUsername;
        HasAdminCredentials = _credentialPassword.Length > 0;
        OnPropertyChanged(nameof(AdminPasswordText));
        OnPropertyChanged(nameof(AdminPasswordToggleText));
        OnPropertyChanged(nameof(CredentialsHint));
    }

    /// <summary>
    /// 核心建的内置管理员密码格式见 <c>DbInitializer.SeedAdminAsync</c>：核心会往日志写一行
    /// <c>Successfully created the built-in admin user "admin" with the initial password: xxx</c>。
    /// 桌面端本来就把这行收进了日志总线，这里顺手解析 —— 覆盖两种不经过自动建号的场景：
    /// 开发态（核心以 Development 运行，直接建 admin/admin）与用户自己配了
    /// <c>OpenList__AdminPassword</c>。
    /// </summary>
    private static readonly Regex SeedPasswordRegex = new(
        "built-in admin user \"(?<user>[^\"]+)\" with the initial password: (?<pwd>.+)$",
        RegexOptions.Compiled);

    private void OnLogEvent(LogEvent e)
    {
        if (e.Source != LogSources.Core) return;
        var m = SeedPasswordRegex.Match(e.Message);
        if (!m.Success) return;

        var user = m.Groups["user"].Value.Trim();
        var pwd = m.Groups["pwd"].Value.Trim();
        if (user.Length == 0 || pwd.Length == 0) return;
        if (string.Equals(_settings.AdminPassword, pwd, StringComparison.Ordinal)) return;  // 核心重启会再打一次

        Dispatcher.UIThread.Post(() =>
        {
            RememberCredentials(user, pwd);
            _log.Info(LogSources.Dashboard, Localizer.Format("dash.credFound", user));
            _notify.Notify("dash.credFoundTitle", "dash.credFoundBody", NoticeLevel.Success, user, pwd);
            _ = UiServices.SetClipboardAsync(pwd);
        });
    }

    /// <summary>生成 12 位随机密码（避开 0/O/1/l/I 这类易混字符），与 CLI 的 <c>admin random</c> 同款。</summary>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return RandomNumberGenerator.GetString(alphabet, 12);
    }

    /// <summary>显示 / 隐藏明文密码。</summary>
    [RelayCommand]
    private void ToggleAdminPassword() => ShowAdminPassword = !ShowAdminPassword;

    [RelayCommand]
    private void OpenInitPage()
    {
        if (!IsRunning) return;
        UpdateChecker.OpenUrl($"{_core.BaseUrl}/#/@init");
    }

    /// <summary>
    /// 复制管理员密码。优先用本机记住的那份（首次自动建号 / 从核心日志读到的），
    /// 其次才是核心 <c>appsettings.json</c> 里的 <c>AdminPassword</c> —— 后者只在建库时被
    /// 读过一次，发布包里通常是空串。
    /// </summary>
    [RelayCommand]
    private async Task CopyAdminPasswordAsync()
    {
        var pwd = _settings.AdminPassword;
        if (string.IsNullOrEmpty(pwd)) pwd = ReadSeedAdminPassword() ?? "";
        if (pwd.Length == 0)
        {
            _log.Error(LogSources.Dashboard, Localizer.Get("dash.passwordNotFound"));
            return;
        }
        await UiServices.SetClipboardAsync(pwd);
        _log.Info(LogSources.Dashboard, Localizer.Get("dash.passwordCopied"));
        _notify.Notify("dash.copiedTitle", "dash.passwordCopiedToast", NoticeLevel.Success);
    }

    /// <summary>
    /// 复制管理员用户名。
    ///
    /// 用户名不一定是 <c>admin</c>：首次建号时用的是配置里的 <c>AdminUsername</c>，从核心日志
    /// 捞到的凭据也带着当时的名字 —— 所以复制界面上<em>显示的那个值</em>，不要硬编码。
    /// </summary>
    [RelayCommand]
    private async Task CopyAdminUsernameAsync()
    {
        var user = AdminUsernameText;
        if (string.IsNullOrWhiteSpace(user)) return;
        await UiServices.SetClipboardAsync(user);
        _log.Info(LogSources.Dashboard, Localizer.Format("dash.usernameCopied", user));
        _notify.Notify("dash.copiedTitle", "dash.usernameCopied", NoticeLevel.Success, user);
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
    /// 调通：已保存 token 时直接用，否则用本机记住的密码（或配置里的初始密码）登录一次。
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
                // 没有可用会话：用本机记住的密码（或配置里的初始密码）换一个临时 token。
                var seed = _settings.AdminPassword is { Length: > 0 } known
                    ? known
                    : ReadSeedAdminPassword() ?? "admin";
                await _api.LoginAsync(AdminUsernameText, seed, null);
            }
            else
            {
                _api.SetToken(previousToken);
            }

            var newPwd = GeneratePassword();
            await _api.ResetAdminPasswordAsync(newPwd);
            RememberCredentials(AdminUsernameText, newPwd);
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
