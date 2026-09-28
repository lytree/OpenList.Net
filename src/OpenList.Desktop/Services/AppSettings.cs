using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenList.Desktop.Services;

/// <summary>单个 Rclone 挂载项配置。</summary>
public sealed class MountConfig
{
    public string Name { get; set; } = "";
    /// <summary>OpenList 的 WebDAV 地址，如 http://127.0.0.1:5244/dav 。</summary>
    public string WebDavUrl { get; set; } = "http://127.0.0.1:5244/dav";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Windows 下盘符如 "X:"；其他平台为本地目录路径。</summary>
    public string MountPoint { get; set; } = "";
    /// <summary>追加给 rclone mount 的自定义旗标（原样透传）。</summary>
    public string ExtraFlags { get; set; } = "";
    /// <summary>核心服务就绪后自动挂载。</summary>
    public bool AutoMount { get; set; }
}

/// <summary>桌面端设置（JSON 持久化到用户应用数据目录）。</summary>
public sealed class AppSettings
{
    /// <summary>OpenList 核心可执行文件路径；留空则自动探测。</summary>
    public string CoreExePath { get; set; } = "";
    /// <summary>核心进程的工作目录（数据库/日志都在这里生成）；留空则用 exe 同级 data 目录。</summary>
    public string DataDir { get; set; } = "";
    public int HttpPort { get; set; } = 5244;
    /// <summary>桌面端启动时自动拉起核心。</summary>
    public bool AutoStartCore { get; set; } = true;
    /// <summary>核心子进程意外退出（非用户主动停止）时自动重启，带指数退避与次数上限。</summary>
    public bool AutoRestartCore { get; set; } = true;
    public bool StartMinimized { get; set; }
    /// <summary>关闭主窗口时驻留系统托盘（不退出）；托盘菜单「退出」才真正结束。</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>开机自启（Windows 写注册表 Run 键）。</summary>
    public bool LaunchOnSystemStartup { get; set; }
    public string RclonePath { get; set; } = "rclone";
    public List<MountConfig> Mounts { get; set; } = [];

    /// <summary>界面主题：light / dark（上游 ThemeSwitcher）。</summary>
    public string Theme { get; set; } = "light";
    /// <summary>界面语言：zh-CN / en（见 Localization.Localizer.Languages）。</summary>
    public string Language { get; set; } = "zh-CN";
    /// <summary>更新页：自动检查更新开关。</summary>
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>挂载页 WinFSP 提示条是否已永久关闭。</summary>
    public bool WinFspTipDismissed { get; set; }

    // 记住的登录态（仅本机保存，方便下次直连）
    public string ServerUrl { get; set; } = "http://127.0.0.1:5244";
    public string? AuthToken { get; set; }
    public string? Username { get; set; }
    /// <summary>核心就绪后自动用记住的凭据登录。</summary>
    public bool AutoLogin { get; set; } = true;

    private static string DirPath
    {
        get
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
            return Path.Combine(dir, "OpenList.Desktop");
        }
    }

    private static string FilePath => Path.Combine(DirPath, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s is not null) return s;
            }
        }
        catch
        {
            // 配置损坏时回退默认值，避免桌面端无法启动。
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DirPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static string SettingsFilePath => FilePath;
}
