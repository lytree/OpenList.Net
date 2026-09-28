using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace OpenList.Desktop.Services;

/// <summary>
/// ViewModel 需要的 UI 能力桥（输入对话框、保存文件、主窗口引用）。
/// 由 App 在初始化主窗口时填充。避免 VM 直接依赖具体 View。
/// </summary>
public static class UiServices
{
    public static Window? MainWindow { get; set; }

    /// <summary>弹出一个单行文本输入对话框，返回 null 表示取消。</summary>
    public static Func<string, string, Task<string?>>? InputAsync { get; set; }

    /// <summary>页面导航请求（主窗口 VM 填充；key 如 "mount"/"settings"）。</summary>
    public static Action<string>? Navigate { get; set; }

    /// <summary>
    /// 弹出「另存为」对话框并写入文本；参数为建议文件名与内容，
    /// 返回实际写入路径，用户取消返回 null。由 App 注入。
    /// </summary>
    public static Func<string, string, Task<string?>>? SaveTextFileAsync { get; set; }

    /// <summary>把文本写入系统剪贴板。</summary>
    public static async Task SetClipboardAsync(string text)
    {
        var top = MainWindow is null ? null : Avalonia.Controls.TopLevel.GetTopLevel(MainWindow);
        if (top?.Clipboard is { } clip)
            await clip.SetTextAsync(text);
    }

    /// <summary>
    /// 默认的另存为实现：用主窗口的存储提供者弹窗，并以 UTF-8 BOM 写出——
    /// 没有 BOM 时 Excel 会把中文 CSV 认成乱码。
    /// </summary>
    public static async Task<string?> SaveTextFileWithPickerAsync(string suggestedName, string content)
    {
        var storage = MainWindow?.StorageProvider;
        if (storage is null) return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt = true,
        });
        if (file is null) return null;

        var path = file.Path.LocalPath;
        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
