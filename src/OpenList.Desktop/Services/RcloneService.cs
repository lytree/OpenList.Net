using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenList.Desktop.Localization;

namespace OpenList.Desktop.Services;

/// <summary>一个挂载项的运行时状态。</summary>
public sealed class RcloneMount
{
    public required MountConfig Config { get; init; }
    public Process? Process { get; set; }
    public bool Running => Process is { HasExited: false };
}

/// <summary>
/// Rclone WebDAV 挂载管理（对标 OpenList-Desktop 的"本地挂载"功能）：
/// 通过 rclone mount 把 OpenList 的 /dav WebDAV 端点挂载为本地盘符/目录。
/// 挂载即一个 rclone 子进程；停止 = 结束进程。密码先经 `rclone obscure` 混淆。
/// </summary>
public sealed class RcloneService : IDisposable
{
    private readonly LogEventBus _log;
    private readonly AppSettings _settings;
    private readonly List<RcloneMount> _mounts = [];

    /// <summary>挂载状态变化（UI 线程外触发）。</summary>
    public event Action? Changed;

    public RcloneService(AppSettings settings, LogEventBus log)
    {
        _settings = settings;
        _log = log;
    }

    public IReadOnlyList<RcloneMount> Mounts => _mounts;

    public RcloneMount? Find(string name) => _mounts.FirstOrDefault(m => m.Config.Name == name);

    public bool IsRunning(string name) => Find(name)?.Running ?? false;

    public async Task<bool> StartAsync(MountConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Name))
        {
            _log.Error(LogSources.Rclone, Localizer.Get("rcl.nameRequired"));
            return false;
        }
        if (string.IsNullOrWhiteSpace(cfg.WebDavUrl))
        {
            _log.Error(LogSources.Rclone, Localizer.Format("rcl.urlRequired", cfg.Name));
            return false;
        }
        if (string.IsNullOrWhiteSpace(cfg.MountPoint))
        {
            _log.Error(LogSources.Rclone, Localizer.Format("rcl.mountPointRequired", cfg.Name));
            return false;
        }
        if (Find(cfg.Name)?.Running == true)
        {
            _log.Info(LogSources.Rclone, Localizer.Format("rcl.alreadyRunning", cfg.Name));
            return true;
        }

        if (!await CheckRcloneAsync()) return false;

        // 非盘符挂载点先确保目录存在
        if (!IsWindowsDrive(cfg.MountPoint))
        {
            try { Directory.CreateDirectory(cfg.MountPoint); }
            catch (Exception ex)
            {
                _log.Error(LogSources.Rclone, Localizer.Format("rcl.mkdirFailed", cfg.Name, ex.Message));
                return false;
            }
        }

        var args = new List<string>
        {
            "mount",
            $":webdav,url={cfg.WebDavUrl.TrimEnd('/')}:",
            Quote(cfg.MountPoint),
            "--webdav-vendor", "other",
            "--vfs-cache-mode", "full",
        };
        if (!string.IsNullOrEmpty(cfg.Username))
            args.AddRange(["--webdav-user", Quote(cfg.Username)]);
        if (!string.IsNullOrEmpty(cfg.Password))
        {
            var obscured = await ObscureAsync(cfg.Password);
            if (obscured is not null) args.AddRange(["--webdav-pass", Quote(obscured)]);
        }
        if (!string.IsNullOrWhiteSpace(cfg.ExtraFlags))
            args.Add(cfg.ExtraFlags.Trim());

        var psi = new ProcessStartInfo
        {
            FileName = _settings.RclonePath,
            Arguments = string.Join(' ', args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var mount = Find(cfg.Name);
            if (mount is null)
            {
                mount = new RcloneMount { Config = cfg };
                _mounts.Add(mount);
            }
            mount.Process = proc;

            // rclone 的输出统一挂 rclone 标签（远程名已在消息前缀里），
            // 这样日志页按来源筛选时不会漏掉具体挂载项的行。
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) _log.Info(LogSources.Rclone, $"[{cfg.Name}] {e.Data}");
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) _log.Error(LogSources.Rclone, $"[{cfg.Name}] {e.Data}");
            };
            proc.Exited += (_, _) =>
            {
                _log.Info(LogSources.Rclone, Localizer.Format("rcl.exited", cfg.Name));
                Changed?.Invoke();
            };

            if (!proc.Start())
            {
                _log.Error(LogSources.Rclone, Localizer.Format("rcl.startFailed", cfg.Name));
                Changed?.Invoke();
                return false;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 给一点时间让进程起稳：300ms 内没退出即视为挂起成功
            await Task.Delay(300);
            if (proc.HasExited)
            {
                _log.Error(LogSources.Rclone,
                    Localizer.Format("rcl.exitedImmediately", cfg.Name, proc.ExitCode));
                Changed?.Invoke();
                return false;
            }
            _log.Info(LogSources.Rclone, Localizer.Format("rcl.mounted", cfg.Name, cfg.MountPoint));
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(LogSources.Rclone, Localizer.Format("rcl.mountFailed", cfg.Name, ex.Message));
            Changed?.Invoke();
            return false;
        }
    }

    public async Task<bool> StopAsync(string name)
    {
        var mount = Find(name);
        if (mount is null || !mount.Running) return false;
        var proc = mount.Process!;
        try
        {
            proc.Kill(entireProcessTree: true);
            await proc.WaitForExitAsync();
            _log.Info(LogSources.Rclone, Localizer.Format("rcl.unmounted", name));
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(LogSources.Rclone, Localizer.Format("rcl.unmountFailed", name, ex.Message));
            return false;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>核心就绪后挂起所有 AutoMount 项。</summary>
    public async void AutoMountAll()
    {
        foreach (var cfg in _settings.Mounts.Where(m => m.AutoMount))
        {
            if (!IsRunning(cfg.Name))
                await StartAsync(cfg);
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var m in _mounts.Where(m => m.Running).ToList())
            await StopAsync(m.Config.Name);
    }

    private async Task<bool> CheckRcloneAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_settings.RclonePath, "version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!;
            var ver = await p.StandardOutput.ReadLineAsync();
            _log.Info(LogSources.Rclone, $"rclone: {ver?.Trim()}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(LogSources.Rclone,
                Localizer.Format("rcl.notRunnable", _settings.RclonePath, ex.Message));
            return false;
        }
    }

    /// <summary>调用 `rclone obscure` 混淆密码。</summary>
    private async Task<string?> ObscureAsync(string plain)
    {
        try
        {
            var psi = new ProcessStartInfo(_settings.RclonePath, "obscure " + Quote(plain))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var val = outp.Trim();
            if (p.ExitCode == 0 && val.Length > 0) return val;
            _log.Error(LogSources.Rclone, Localizer.Get("rcl.obscureFailed"));
            return null;
        }
        catch (Exception ex)
        {
            _log.Error(LogSources.Rclone, Localizer.Format("rcl.obscureError", ex.Message));
            return null;
        }
    }

    private static bool IsWindowsDrive(string mp) =>
        Regex.IsMatch(mp.Trim(), @"^[A-Za-z]:(\\.*)?$") || mp.Trim() == "*";

    private static string Quote(string s) =>
        s.Contains(' ') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;

    public void Dispose() => StopAllAsync().GetAwaiter().GetResult();
}
