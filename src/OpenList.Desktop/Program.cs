using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using OpenList.Desktop.Localization;

namespace OpenList.Desktop;

internal static class Program
{
    /// <summary>
    /// 进程级命名互斥体。同一个用户会话里只允许跑一个桌面实例——
    /// 否则两个实例会各自拉起一个 OpenList.Server 争抢同一个监听端口，
    /// 表现为"第二个实例的核心永远起不来"。
    /// </summary>
    private const string InstanceMutexName = "OpenList.Desktop.SingleInstance";

    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        if (!TryAcquireSingleInstance())
        {
            NotifyAlreadyRunning();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ReleaseSingleInstance();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions())
        .LogToTrace();

    /// <summary>取得单实例所有权；已有实例在跑时返回 false。</summary>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            if (createdNew) return true;

            // 未取得所有权则不释放（否则会抛），仅关闭句柄
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch
        {
            // 互斥体不可用（受限沙箱等）时放行，避免桌面端彻底打不开
            _instanceMutex = null;
            return true;
        }
    }

    private static void ReleaseSingleInstance()
    {
        if (_instanceMutex is null) return;
        try
        {
            _instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 未持有所有权（例如上面走了放行分支）——无需释放
        }
        finally
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
    }

    /// <summary>提示用户已有实例在运行（Avalonia 尚未启动，故直接调系统弹窗）。</summary>
    private static void NotifyAlreadyRunning()
    {
        var body = Localizer.Get("app.alreadyRunningBody");
        var title = Localizer.Get("app.alreadyRunningTitle");

        if (OperatingSystem.IsWindows())
        {
            // MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST
            const uint flags = 0x00000000u | 0x00000040u | 0x00010000u | 0x00040000u;
            MessageBoxW(IntPtr.Zero, body, title, flags);
            return;
        }

        Console.Error.WriteLine(body);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
