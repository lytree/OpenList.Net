using System.Diagnostics;
using OpenList.Desktop.Localization;

namespace OpenList.Desktop.Services;

/// <summary>核心服务状态。</summary>
public enum CoreStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>
/// 核心状态变更通知。只带文案 key 与参数，由 UI 层按当前语言渲染，
/// 这样服务层不必关心本地化，语言切换后新通知也自动是新语言。
/// </summary>
public sealed record CoreNotice(string TitleKey, string BodyKey, object?[] BodyArgs, bool IsError);

/// <summary>
/// OpenList 核心子进程管理（对标 OpenList-Desktop 的"服务管理"功能）：
/// 启动 / 停止 / 重启 OpenList.Server 进程，采集其 stdout/stderr 到日志总线，
/// 通过 GET /ping 探活并跟踪运行时长。端口通过 OpenList__Scheme__HttpPort
/// 环境变量覆盖核心默认配置（核心端支持 OpenList__ 前缀的环境变量覆盖）。
///
/// <para><b>崩溃自愈</b>：核心若<b>非本端主动停止</b>而退出，会按指数退避自动重启，
/// 连续失败到 <see cref="MaxRestartAttempts"/> 次后放弃并通知用户（见 <see cref="Notice"/>）。</para>
/// </summary>
public sealed class OpenListCoreService : IDisposable
{
    /// <summary>连续自动重启次数上限，超过即放弃（避免无限崩溃循环）。</summary>
    public const int MaxRestartAttempts = 5;

    private readonly LogEventBus _log;
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private Process? _process;
    private DateTimeOffset? _startedAt;
    private CoreStatus _status = CoreStatus.Stopped;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>本次退出是否由本端主动触发（用户停止 / 就绪超时放弃 / 退出程序）。</summary>
    private volatile bool _expectExit;

    /// <summary>用户显式要求保持停止（或程序正在退出），用于取消排队中的自动重启。</summary>
    private volatile bool _restartCancelled;

    private volatile bool _disposed;
    private int _restartAttempts;

    public event Action<CoreStatus>? StatusChanged;

    /// <summary>需要提示用户的状况（启动成功、意外退出、自动重启、放弃重启等）。</summary>
    public event Action<CoreNotice>? Notice;

    public CoreStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }

    public OpenListCoreService(AppSettings settings, LogEventBus log)
    {
        _settings = settings;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public string BaseUrl => $"http://127.0.0.1:{_settings.HttpPort}";

    /// <summary>核心子进程（供监控页采集 CPU/内存等）；未运行或已退出为 null。</summary>
    public Process? CurrentProcess => _process is { HasExited: false } p ? p : null;

    /// <summary>核心本次启动时间（监控运行时长用）。</summary>
    public DateTimeOffset? StartedAt => _startedAt;

    /// <summary>已连续自动重启的次数。</summary>
    public int RestartAttempts => _restartAttempts;

    public string? UptimeText
    {
        get
        {
            if (Status != CoreStatus.Running || _startedAt is null) return null;
            var t = DateTimeOffset.Now - _startedAt.Value;
            var clock = $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
            return t.Days > 0
                ? Localizer.Format("status.uptimeDays", t.Days, clock)
                : clock;
        }
    }

    /// <summary>核心 exe 是否已就绪可启动。</summary>
    public bool CanStart => Status == CoreStatus.Stopped && ResolveCorePath() is not null;

    /// <summary>解析核心可执行文件路径：优先用户设置，其次自动探测。</summary>
    public string? ResolveCorePath()
    {
        var configured = _settings.CoreExePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        // 候选：桌面 exe 同级 → 发布目录 server → 开发目录相邻项目
        string[] candidates =
        [
            Path.Combine(baseDir, "OpenList.Server.exe"),
            Path.Combine(baseDir, "OpenList.Server"),
            Path.Combine(baseDir, "server", "OpenList.Server.exe"),
            Path.Combine(baseDir, "server", "OpenList.Server"),
            Path.Combine(baseDir, "..", "..", "..", "..", "OpenList.Server", "bin", "Debug", "net10.0", "OpenList.Server.exe"),
        ];
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public string ResolveDataDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.DataDir))
        {
            Directory.CreateDirectory(_settings.DataDir);
            return _settings.DataDir;
        }
        // 默认：桌面端 exe 同级 data 目录（发布形态），开发态落在桌面项目输出旁
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task StartAsync()
    {
        if (Status != CoreStatus.Stopped) return;
        await _gate.WaitAsync();
        try
        {
            if (Status != CoreStatus.Stopped) return;

            var exe = ResolveCorePath();
            if (exe is null)
            {
                _log.Error(LogSources.Core, Localizer.Get("dash.coreExeMissing"));
                Raise(LogSources.Core, "notify.startFailedTitle", "dash.coreExeMissing", [], isError: true);
                return;
            }

            Status = CoreStatus.Starting;
            _expectExit = false;
            _restartCancelled = false;
            _log.Info(LogSources.Core, Localizer.Format("dash.coreStarting", exe));

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = ResolveDataDir(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // 核心读取 OpenList__ 前缀环境变量覆盖 appsettings；
            // 但 Kestrel 实际监听地址由 ASPNETCORE_URLS 决定，必须一并注入
            psi.Environment["OpenList__Scheme__HttpPort"] = _settings.HttpPort.ToString();
            psi.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{_settings.HttpPort}";

            // 发布形态（桌面程序根目录 + server/ + frontend/dist/）：把前端 dist 的绝对路径
            // 注入给核心，覆盖 appsettings.json 里的相对 DistDir（核心的工作目录是 data 目录）。
            var distDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", "frontend", "dist"));
            if (Directory.Exists(distDir))
                psi.Environment["OpenList__DistDir"] = distDir;

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Info(LogSources.Core, e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.Error(LogSources.Core, e.Data); };
            proc.Exited += (_, _) => OnProcessExited(proc);

            if (!proc.Start())
            {
                Status = CoreStatus.Stopped;
                _log.Error(LogSources.Core, Localizer.Get("dash.coreStartProcessFailed"));
                Raise(LogSources.Core, "notify.startFailedTitle", "dash.coreStartProcessFailed", [], isError: true);
                return;
            }
            _process = proc;
            _startedAt = DateTimeOffset.Now;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 轮询 /ping 直到就绪（最长 60 秒，覆盖数据库迁移 + 存储加载）
            var deadline = DateTimeOffset.Now.AddSeconds(60);
            while (DateTimeOffset.Now < deadline)
            {
                if (proc.HasExited)
                {
                    // 正常路径下退出事件已处理过（含自动重启排队）；这里兜住事件尚未投递的窗口。
                    // OnProcessExited 靠 ReferenceEquals(_process, proc) 保证幂等。
                    OnProcessExited(proc);
                    return;
                }
                if (await PingOnceAsync())
                {
                    Status = CoreStatus.Running;
                    _restartAttempts = 0;   // 成功就绪即清零退避计数
                    _log.Info(LogSources.Core, Localizer.Format("dash.coreReady", BaseUrl));
                    Raise(LogSources.Core, "notify.coreStartedTitle", "notify.coreStartedBody", [BaseUrl], isError: false);
                    return;
                }
                await Task.Delay(500);
            }

            // 探活超时：进程还活着但服务不可用。必须主动结束它，
            // 否则会留下一个占着端口的孤儿进程，且 _process 与 Status 状态不一致。
            _log.Error(LogSources.Core, Localizer.Get("dash.coreReadyTimeout"));
            _expectExit = true;
            Status = CoreStatus.Stopped;
            TryKill(proc);
        }
        catch (Exception ex)
        {
            Status = CoreStatus.Stopped;
            _log.Error(LogSources.Core, Localizer.Format("dash.coreStartFailed", ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        // 用户显式要求保持停止：作废排队中的自动重启
        _restartCancelled = true;
        _restartAttempts = 0;

        if (_process is null || _process.HasExited)
        {
            Status = CoreStatus.Stopped;
            return;
        }
        await _gate.WaitAsync();
        try
        {
            if (_process is null || _process.HasExited) { Status = CoreStatus.Stopped; return; }
            Status = CoreStatus.Stopping;
            _log.Info(LogSources.Core, Localizer.Get("dash.coreStopping"));
            _expectExit = true;
            TryKill(_process);
            await _process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _log.Error(LogSources.Core, Localizer.Format("dash.coreStopFailed", ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(300);
        await StartAsync();
    }

    private void OnProcessExited(Process proc)
    {
        var exitCode = -1;
        try { exitCode = proc.ExitCode; } catch { /* 退出码可能不可读 */ }

        // 幂等：退出事件与 StartAsync 的兜底调用可能都会进来，只有第一次生效
        if (!ReferenceEquals(_process, proc)) return;

        var expected = _expectExit;
        _expectExit = false;
        _process = null;
        _startedAt = null;
        Status = CoreStatus.Stopped;

        _log.Info(LogSources.Core, Localizer.Format("dash.coreExited", exitCode));

        if (expected)
        {
            if (!_disposed)
                Raise(LogSources.Core, "notify.coreStoppedTitle", "notify.coreStoppedBody", [], isError: false);
            return;
        }

        // ---- 非预期退出：进入自愈流程 ----
        Raise(LogSources.Core, "notify.coreExitedTitle", "notify.coreExitedBody", [exitCode], isError: true);

        if (!_settings.AutoRestartCore || _disposed || _restartCancelled)
            return;

        var attempt = ++_restartAttempts;
        if (attempt > MaxRestartAttempts)
        {
            Raise(LogSources.Core, "notify.gaveUpTitle", "notify.gaveUpBody", [MaxRestartAttempts], isError: true);
            return;
        }

        // 指数退避 1s → 2s → 4s → 8s → 16s（上限 30s）
        var delaySeconds = (int)Math.Min(30, Math.Pow(2, attempt - 1));
        Raise(LogSources.Core, "notify.restartingTitle", "notify.restartingBody", [attempt, delaySeconds], isError: true);
        _ = RestartAfterDelayAsync(TimeSpan.FromSeconds(delaySeconds));
    }

    private async Task RestartAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            if (_disposed || _restartCancelled) return;
            await StartAsync();
        }
        catch (Exception ex)
        {
            _log.Error(LogSources.Core, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Raise(string source, string titleKey, string bodyKey, object?[] args, bool isError)
    {
        try { Notice?.Invoke(new CoreNotice(titleKey, bodyKey, args, isError)); }
        catch { /* 订阅方异常不影响核心管理 */ }

        if (isError)
            _log.Warn(source, Localizer.Format(bodyKey, args));
    }

    private static void TryKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (Exception)
        {
            // 兜底：Windows 下用 taskkill 强杀进程树
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var k = Process.Start(new ProcessStartInfo("taskkill", $"/PID {p.Id} /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    k?.WaitForExit(5000);
                }
            }
            catch { /* 已尽力 */ }
        }
    }

    /// <summary>探活并返回延迟（毫秒）；不可达返回 -1。</summary>
    public async Task<long> PingLatencyAsync()
    {
        var sw = Stopwatch.StartNew();
        if (await PingOnceAsync()) return sw.ElapsedMilliseconds;
        return -1;
    }

    private async Task<bool> PingOnceAsync()
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/ping");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        // 桌面端退出时带走核心子进程，避免孤儿进程；并阻止任何排队中的自动重启
        _disposed = true;
        _restartCancelled = true;
        _expectExit = true;
        if (_process is { HasExited: false } p) TryKill(p);
        _http.Dispose();
        _gate.Dispose();
    }
}
