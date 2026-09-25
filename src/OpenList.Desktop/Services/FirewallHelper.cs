using System.Diagnostics;

namespace OpenList.Desktop.Services;

/// <summary>
/// Windows 防火墙放行端口（上游 QuickActionsCard 的 netsh firewall 功能）。
/// 规则名固定为 OpenList-Desktop，便于检测与移除。
/// </summary>
public static class FirewallHelper
{
    private const string RuleName = "OpenList-Desktop";

    private static (string Exe, string Args) Run(string args) => ("netsh.exe", args);

    public static bool IsSupported()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"));
        }
        catch { return false; }
    }

    /// <summary>检测放行规则是否已存在。</summary>
    public static bool IsPortAllowed()
    {
        if (!IsSupported()) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"advfirewall firewall show rule name=\"{RuleName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Contains("规则名称", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Rule Name", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>添加放行规则。返回 false 表示失败。</summary>
    public static bool AllowPort(int port)
    {
        if (!IsSupported()) return false;
        try
        {
            var (exe, args) = Run(
                $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port}");
            return RunNetsh(exe, args);
        }
        catch { return false; }
    }

    /// <summary>移除放行规则。返回 false 表示失败。</summary>
    public static bool RemoveRule()
    {
        if (!IsSupported()) return false;
        try
        {
            var (exe, args) = Run($"advfirewall firewall delete rule name=\"{RuleName}\"");
            return RunNetsh(exe, args);
        }
        catch { return false; }
    }

    private static bool RunNetsh(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(8000);
        return p.ExitCode == 0;
    }
}
