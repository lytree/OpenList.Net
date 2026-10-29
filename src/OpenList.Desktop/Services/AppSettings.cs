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
    /// <summary>
    /// 心跳采样间隔（秒）。桌面端用它决定 <c>GET /ping</c> 的探测频率：
    /// 主页心跳图与监控页延迟值都按这个节奏更新。间隔越短曲线越密、也越频繁地
    /// 唤醒 HttpClient 与服务端线程；默认 10 秒对「看个趋势」已经够用。
    /// 非法值由 <see cref="AppSettings"/> 载入时夹取到 3–300 秒。
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;
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

    /// <summary>
    /// 本机记住的管理员账号名。首次启动自动初始化时写入，也用于自动登录。
    /// 与 <see cref="Username"/> 的区别：后者是「上次登录用的账号」，这里特指内置管理员。
    /// </summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// 本机记住的管理员明文密码 —— <b>只为让用户看得见自己第一次的密码</b>。
    ///
    /// <para>
    /// 数据库里只有哈希，所以「初始密码」一旦没人记下来就永久丢失：桌面端把核心当子进程
    /// 拉起（GUI 无控制台），核心即便生成了密码也只会打在 stdout 里。因此桌面端在
    /// 「首次自动建号」或「从核心日志里读到初始密码」时把它存在这里，主页据此展示、复制。
    /// </para>
    /// <para>
    /// 它不是权威来源：用户在网页里改过密码后这里就过期了。改密入口见主页的「重置密码」。
    /// 落盘位置是 <c>%APPDATA%/OpenList.Desktop/settings.json</c>（与挂载密码同档），仅本机。
    /// </para>
    /// </summary>
    public string AdminPassword { get; set; } = "";

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

    /// <summary>
    /// 心跳间隔在 <see cref="Save"/> 后发生变化时触发。采样计时器订阅它，
    /// 这样在设置页改完立即生效，不必重启桌面端。
    /// </summary>
    public event Action? HeartbeatIntervalChanged;

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
                if (s is not null) return Normalize(s);
            }
        }
        catch
        {
            // 配置损坏时回退默认值，避免桌面端无法启动。
        }
        return new AppSettings();
    }

    /// <summary>
    /// 把越界/非法的数值夹回可用区间。放在载入与保存两处兜底，这样即便用户手改了
    /// settings.json 也不会让心跳间隔变成 0（= 每帧 ping）或负数（Timer 直接抛）。
    /// </summary>
    private static AppSettings Normalize(AppSettings s)
    {
        if (s.HeartbeatIntervalSeconds is < MinHeartbeatSeconds or > MaxHeartbeatSeconds)
            s.HeartbeatIntervalSeconds = new AppSettings().HeartbeatIntervalSeconds;
        if (s.HttpPort is <= 0 or > 65535) s.HttpPort = 5244;
        return s;
    }

    /// <summary>心跳间隔下限（秒）：再密也只是徒增开销，没有实际意义。</summary>
    public const int MinHeartbeatSeconds = 3;

    /// <summary>心跳间隔上限（秒）：超过这个量级不如让用户重启一次核心。</summary>
    public const int MaxHeartbeatSeconds = 300;

    /// <summary>取夹取后的心跳间隔，供计时器直接使用。</summary>
    public TimeSpan HeartbeatInterval =>
        TimeSpan.FromSeconds(Math.Clamp(HeartbeatIntervalSeconds, MinHeartbeatSeconds, MaxHeartbeatSeconds));

    public void Save()
    {
        var before = HeartbeatIntervalSeconds;
        Normalize(this);
        Directory.CreateDirectory(DirPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        if (HeartbeatIntervalSeconds != before)
        {
            try { HeartbeatIntervalChanged?.Invoke(); }
            catch { /* 订阅方异常不影响保存 */ }
        }
    }

    public static string SettingsFilePath => FilePath;
}
