using Microsoft.Win32;
using System.Runtime.Versioning;

namespace OpenList.Desktop.Services;

/// <summary>开机自启（Windows：HKCU\...\Run 注册表键；其他平台暂不支持）。</summary>
public static class AutostartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenList.Desktop";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return IsEnabledCore();
    }

    public static void SetEnabled(bool enabled, string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        SetEnabledCore(enabled, exePath);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledCore()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    [SupportedOSPlatform("windows")]
    private static void SetEnabledCore(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{exePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
