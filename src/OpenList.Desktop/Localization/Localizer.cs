using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.Localization;

/// <summary>语言选项（code 为 BCP-47 语言代码）。</summary>
public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// 运行时本地化。
///
/// 视图侧统一用 <c>Text="{DynamicResource L.nav.home}"</c> 取值：<see cref="Apply"/> 会就地
/// 改写词典内容并触发 <see cref="ResourceDictionary.ResourcesChanged"/>，Avalonia 的
/// DynamicResource 机制随即刷新所有引用点，无需重启窗口或重建视图。
///
/// 非 XAML 场景（VM 计算属性、托盘提示、通知文案）走 <see cref="Get"/> /
/// <see cref="Format"/>；语言变化时订阅 <see cref="LanguageChanged"/> 重新通知绑定。
/// </summary>
public sealed class Localizer
{
    /// <summary>资源键前缀，避免与 App.axaml 里的 Brush.* / I.* 撞名。</summary>
    public const string KeyPrefix = "L.";

    private static readonly Lazy<Localizer> LazyInstance = new(() => new Localizer());
    public static Localizer Instance => LazyInstance.Value;

    private readonly Dictionary<string, string> _current;
    private bool _attached;

    private Localizer()
    {
        // 首次访问即按已保存的偏好初始化，使 Program.Main（Avalonia 起来之前）也能取到正确语言
        Language = Normalize(AppSettings.Load().Language);
        _current = new Dictionary<string, string>(Strings.For(Language), StringComparer.Ordinal);
    }

    /// <summary>当前语言代码（zh-CN / en）。</summary>
    public string Language { get; private set; }

    /// <summary>供 XAML 侧合并进 Application.Resources 的词典（键带 <see cref="KeyPrefix"/>）。</summary>
    public ResourceDictionary Dictionary { get; } = new();

    /// <summary>语言已切换（VM 借此重新通知计算属性）。在 UI 线程触发。</summary>
    public event Action? LanguageChanged;

    /// <summary>可选语言列表（下拉框数据源）。</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("zh-CN", "\u7b80\u4f53\u4e2d\u6587"),
        new("en", "English"),
    ];

    /// <summary>取一条文案；key 缺失时回退中文、再回退 key 本身（便于发现漏翻）。</summary>
    public static string Get(string key)
    {
        var self = Instance;
        if (self._current.TryGetValue(key, out var text)) return text;
        return Strings.ZhCn.TryGetValue(key, out var fallback) ? fallback : key;
    }

    /// <summary>取一条带占位符的文案，如 Format("notify.coreExited", 1)。</summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// 应用到宿主：把（重新填充过的）词典挂到 <see cref="Application.Resources"/> 上。
    /// 须在创建窗口之前调用一次。
    /// </summary>
    public static void AttachTo(Application app)
    {
        var self = Instance;
        self.FillDictionary();
        if (self._attached) return;
        self._attached = true;
        app.Resources.MergedDictionaries.Add(self.Dictionary);
    }

    /// <summary>
    /// 切换语言（并返回是否真的发生了变化）。调用方负责把结果持久化到 <see cref="AppSettings"/>。
    ///
    /// <b>必须在 UI 线程调用</b>：内部会改写 <see cref="Dictionary"/> 这一 Avalonia
    /// 资源字典，非 UI 线程写入会抛线程亲和性异常。
    /// </summary>
    public bool Apply(string? code)
    {
        var normalized = Normalize(code);
        if (normalized == Language) return false;

        Language = normalized;
        _current.Clear();
        foreach (var (k, v) in Strings.For(normalized))
            _current[k] = v;

        FillDictionary();
        LanguageChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 把当前词典内容同步到 XAML 资源表；逐个赋值会触发 <c>ResourcesChanged</c>，
    /// DynamicResource 随即重新解析。两套词典 key 集合必须完全一致
    /// （见 <c>Strings.ZhCn</c> / <c>Strings.En</c>），否则切换后会残留上一语言的键。
    /// </summary>
    private void FillDictionary()
    {
        foreach (var (key, text) in _current)
            Dictionary[KeyPrefix + key] = text;
    }

    /// <summary>把任意输入规整为受支持的语言代码；不识别时回落到 zh-CN。</summary>
    private static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "zh-CN";
        var trimmed = code.Trim();
        foreach (var opt in Languages)
        {
            if (string.Equals(opt.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                return opt.Code;
        }
        // 允许传 "en-US" 这类更具体的 tag，按主语言匹配
        var primary = trimmed.Split('-', '_')[0];
        foreach (var opt in Languages)
        {
            if (string.Equals(opt.Code.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase))
                return opt.Code;
        }
        return "zh-CN";
    }
}
