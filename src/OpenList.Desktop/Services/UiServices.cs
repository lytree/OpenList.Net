using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace OpenList.Desktop.Services;

/// <summary>
/// ViewModel 需要的 UI 能力桥（输入对话框、主窗口引用）。
/// 由 App 在初始化主窗口时填充。避免 VM 直接依赖具体 View。
/// </summary>
public static class UiServices
{
    public static Window? MainWindow { get; set; }

    /// <summary>弹出一个单行文本输入对话框，返回 null 表示取消。</summary>
    public static Func<string, string, Task<string?>>? InputAsync { get; set; }

    /// <summary>页面导航请求（主窗口 VM 填充；key 如 "mount"/"settings"）。</summary>
    public static Action<string>? Navigate { get; set; }

    /// <summary>把文本写入系统剪贴板。</summary>
    public static async Task SetClipboardAsync(string text)
    {
        var top = MainWindow is null ? null : Avalonia.Controls.TopLevel.GetTopLevel(MainWindow);
        if (top?.Clipboard is { } clip)
            await clip.SetTextAsync(text);
    }
}
