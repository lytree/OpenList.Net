using System.Diagnostics;

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
/// OpenList 核心子进程管理（对标 OpenList-Desktop 的"服务管理"功能）：
/// 启动 / 停止 / 重启 OpenList.Server 进程，采集其 stdout/stderr 到日志总线，
/// 通过 GET /ping 探活并跟踪运行时长。端口通过 OpenList__Scheme__HttpPort
/// 环境变量覆盖核心默认配置（核心端支持 OpenList__ 前缀的环境变量覆盖）。
/// </summary>
public sealed class OpenListCoreService : IDisposable
{
    private readonly LogEventBus _log;
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private Process? _process;
    private DateTimeOffset? _startedAt;
    private CoreStatus _status = CoreStatus.Stopped;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action<CoreStatus>? StatusChanged;

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

    public string? UptimeText
    {
        get
        {
            if (Status != CoreStatus.Running || _startedAt is null) return null;
            var t = DateTimeOffset.Now - _startedAt.Value;
            return t.Days > 0
                ? $"{t.Days}天 {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
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
                _log.Error("核心", "未找到 OpenList 核心可执行文件，请在「设置」里指定路径");
                return;
            }

            Status = CoreStatus.Starting;
            _log.Info("核心", $"启动核心: {exe}");

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
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Info("核心", e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.Error("核心", e.Data); };
            proc.Exited += (_, _) => OnProcessExited(proc);

            if (!proc.Start())
            {
                Status = CoreStatus.Stopped;
                _log.Error("核心", "进程启动失败");
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
                    Status = CoreStatus.Stopped;
                    _log.Error("核心", $"核心异常退出，退出码 {proc.ExitCode}");
                    return;
                }
                if (await PingOnceAsync()) 
                {
                    Status = CoreStatus.Running;
                    _log.Info("核心", $"核心已就绪: {BaseUrl}");
                    return;
                }
                await Task.Delay(500);
            }
            Status = CoreStatus.Stopped;
            _log.Error("核心", "等待核心就绪超时（60 秒）");
        }
        catch (Exception ex)
        {
            Status = CoreStatus.Stopped;
            _log.Error("核心", $"启动失败: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
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
            _log.Info("核心", "停止核心…");
            TryKill(_process);
            await _process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _log.Error("核心", $"停止失败: {ex.Message}");
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
        try
        {
            _log.Info("核心", $"核心进程退出，退出码 {proc.ExitCode}");
        }
        catch { /* 退出码可能不可读 */ }
        if (ReferenceEquals(_process, proc))
        {
            _process = null;
            _startedAt = null;
            Status = CoreStatus.Stopped;
        }
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
        // 桌面端退出时带走核心子进程，避免孤儿进程
        if (_process is { HasExited: false } p) TryKill(p);
        _http.Dispose();
        _gate.Dispose();
    }
}
