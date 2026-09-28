using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenList.Desktop.Localization;

namespace OpenList.Desktop.ViewModels;

/// <summary>
/// 所有页面 VM 的基类。
///
/// 这里额外承担一件事：订阅 <see cref="Localizer.LanguageChanged"/>，在语言切换时把
/// 自身<b>所有</b>属性标记为已变更（<c>PropertyChanged("")</c>）。这样 VM 里由代码拼出来的
/// 文案（状态行、统计行、"未安装"之类）无需逐个手写通知即可即时刷新；
/// XAML 里用 <c>{DynamicResource L.*}</c> 的静态文案则由资源字典变更驱动。
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    protected ViewModelBase()
    {
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) OnPropertyChanged(string.Empty);
        else Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));
    }
}
