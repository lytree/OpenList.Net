using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenList.Server.Hosting;

/// <summary>
/// 本进程的构建与运行时信息，供管理台「关于」页（<c>GET /api/admin/system/info</c>）使用。
///
/// 版本号的唯一来源是程序集的 <see cref="AssemblyInformationalVersionAttribute"/>：
/// 发布时由 cake（<c>build/build.cs</c> 的 <c>WithVersionProps</c>）注入 <c>--semver</c>，
/// 同时 SDK 会把 <c>SourceRevisionId</c> 拼在 <c>+</c> 之后，形如
/// <c>4.2.6+07d789e2b9a8…</c>。开发态未注入时是 csproj/SDK 的默认 <c>1.0.0</c>。
///
/// 这里刻意**不**读 <see cref="Assembly.GetName"/>().Version：那是四段 AssemblyVersion，
/// 与 release tag 对不上（tag 是 semver，可能带 <c>-beta.1</c> 预发布后缀）。
/// </summary>
public static class BuildInfo
{
    /// <summary>形如 <c>4.2.6</c> 或 <c>0.0.1-e2e</c>。</summary>
    public static string Version { get; } = Parse(InformationalVersion()).Version;

    /// <summary>
    /// 提交 SHA（<c>+</c> 之后的部分），完整 40 位；未注入时为空串。
    /// 显示用的短 SHA 由前端截断 —— 完整值留在 API 里便于对照 release。
    /// </summary>
    public static string Commit { get; } = Parse(InformationalVersion()).Commit;

    /// <summary>形如 <c>v4.2.6</c>，与 GitHub release 的 tag 直接可比。</summary>
    public static string Tag => "v" + Version;

    /// <summary>构建配置：<c>Debug</c> / <c>Release</c>（由本程序集编译期符号决定）。</summary>
    public static string Configuration { get; } =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>
    /// 程序集文件自身的写入时间（UTC）—— 即「后端是什么时候构建的」。
    /// 单文件发布时程序集在包内、可能取不到，此时返回 <c>null</c>（前端隐藏该行）。
    /// </summary>
    public static DateTimeOffset? AssemblyBuiltAtUtc()
    {
        try
        {
            var location = typeof(BuildInfo).Assembly.Location;
            if (string.IsNullOrEmpty(location)) return null;
            var info = new FileInfo(location);
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>
    /// 进程启动时间（UTC）。取不到时返回 <c>null</c>，前端显示「—」而不是编一个数字。
    /// </summary>
    public static DateTimeOffset? ProcessStartedAtUtc()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (InvalidOperationException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>运行时描述，如 <c>.NET 10.0.0</c>。</summary>
    public static string Framework => RuntimeInformation.FrameworkDescription;

    /// <summary>操作系统描述，如 <c>Microsoft Windows 10.0.22631</c>。</summary>
    public static string Os => RuntimeInformation.OSDescription;

    /// <summary>进程架构，如 <c>X64</c> / <c>Arm64</c>。</summary>
    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString();

    /// <summary>
    /// 把 <c>AssemblyInformationalVersion</c> 拆成 (版本, 提交)。
    ///
    /// 规则只有一条：**第一个 <c>+</c> 左边是版本、右边是构建元数据**（也就是 SourceRevisionId）。
    /// 没有 <c>+</c> 就是纯版本号；空值回落到 <c>0.0.0</c>。
    /// 之所以不接受「空版本 + 有提交」这种形态：semver 本身不允许空版本，说明注入出了问题，
    /// 显示 <c>0.0.0</c> 比显示一个只有 sha 的怪字符串更容易被认出来。
    /// </summary>
    /// <param name="informationalVersion">程序集里的版本串，可为 <c>null</c>。</param>
    public static (string Version, string Commit) Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return ("0.0.0", "");

        var raw = informationalVersion.Trim();
        var plus = raw.IndexOf('+');
        if (plus < 0) return (raw, "");

        var version = raw[..plus].Trim();
        var commit = raw[(plus + 1)..].Trim();
        return (version.Length == 0 ? "0.0.0" : version, commit);
    }

    private static string? InformationalVersion() =>
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
}
