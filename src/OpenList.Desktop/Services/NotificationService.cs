using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Localization;

namespace OpenList.Desktop.Services;

/// <summary>通知级别（决定 toast 的描边色与图标语气）。</summary>
public enum NoticeLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>一条待显示的浮层通知。</summary>
public partial class ToastItem : ObservableObject
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required NoticeLevel Level { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public bool IsInfo => Level == NoticeLevel.Info;
    public bool IsSuccess => Level == NoticeLevel.Success;
    public bool IsWarning => Level == NoticeLevel.Warning;
    public bool IsError => Level == NoticeLevel.Error;
}

/// <summary>
/// 通知中心（对标上游"系统托盘通知"）。
///
/// <para>Avalonia 的 <see cref="Avalonia.Controls.TrayIcon"/> 只暴露菜单与提示文本，
/// 没有气泡 API，因此这里用两条通道送达用户：</para>
/// <list type="number">
///   <item>托盘图标提示文本实时反映核心状态（鼠标悬停可见，窗口隐藏时也有效）；</item>
///   <item>窗口内右下角浮层 toast（窗口可见时直达，定时自动消失）。</item>
/// </list>
/// <para>文案用 key 传入，显示时才按当前语言解析，因此切换语言后新通知自动是新语言。</para>
/// </summary>
public sealed partial class NotificationService : IDisposable
{
    private const int MaxVisible = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    private readonly LogEventBus _log;
    private readonly DispatcherTimer _reaper;

    /// <summary>当前显示的 toast（绑定到 MainWindow 的浮层）。仅 UI 线程访问。</summary>
    public ObservableCollection<ToastItem> Items { get; } = [];

    public NotificationService(LogEventBus log)
    {
        _log = log;
        _reaper = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reaper.Tick += (_, _) => Reap();
        _reaper.Start();
    }

    /// <summary>推送一条通知；titleKey / bodyKey 为词典 key，args 填占位符。</summary>
    public void Notify(string titleKey, string bodyKey, NoticeLevel level, params object?[] args)
    {
        var title = Localizer.Get(titleKey);
        var body = args.Length > 0 ? Localizer.Format(bodyKey, args) : Localizer.Get(bodyKey);

        void Add()
        {
            Items.Add(new ToastItem
            {
                Id = Guid.NewGuid(),
                Title = title,
                Body = body,
                Level = level,
                ExpiresAt = DateTimeOffset.Now + Lifetime,
            });
            while (Items.Count > MaxVisible)
                Items.RemoveAt(0);
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    /// <summary>
    /// 同步托盘图标的悬停提示（窗口隐藏时用户唯一的可见状态）。
    ///
    /// 本方法会从<b>任意线程</b>被调用——核心的 <c>Process.Exited</c> 回调跑在线程池线程上，
    /// 而 <see cref="Avalonia.Controls.TrayIcon.ToolTipText"/> 是带 UI 线程亲和性的
    /// StyledProperty，直接赋值会抛 InvalidOperationException 并终结进程。故在此统一封送。
    /// </summary>
    public void SetTrayStatus(string statusText)
    {
        void Apply()
        {
            var tray = App.Tray;
            if (tray is null) return;
            tray.ToolTipText = Localizer.Format("tray.tooltip", statusText);
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>把核心状态同步到托盘提示。</summary>
    public void SetCoreStatus(CoreStatus status) => SetTrayStatus(Localizer.Get(CoreStatusKey(status)));

    /// <summary>核心状态 → 词典 key（界面与托盘共用一套措辞）。</summary>
    public static string CoreStatusKey(CoreStatus status) => status switch
    {
        CoreStatus.Starting => "status.starting",
        CoreStatus.Running => "status.running",
        CoreStatus.Stopping => "status.stopping",
        _ => "status.stopped",
    };

    /// <summary>按当前语言刷新托盘提示（语言切换后调用）。</summary>
    public void RefreshTrayStatus(string statusText) => SetTrayStatus(statusText);

    [RelayCommand]
    private void Dismiss(ToastItem? item)
    {
        if (item is not null) Items.Remove(item);
    }

    private void Reap()
    {
        var now = DateTimeOffset.Now;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].ExpiresAt <= now)
                Items.RemoveAt(i);
        }
    }

    public void Dispose() => _reaper.Stop();
}
