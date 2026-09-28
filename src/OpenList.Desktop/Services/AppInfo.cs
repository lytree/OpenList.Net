using System.Reflection;

namespace OpenList.Desktop.Services;

/// <summary>
/// 桌面端自身的仓库与版本信息。
///
/// 版本号取自程序集的 <see cref="AssemblyInformationalVersionAttribute"/>：发布时由
/// cake（<c>build/build.cs</c> 的 <c>WithVersionProps</c>）注入 <c>--semver</c>，
/// 形如 <c>4.2.6+Sha.abc1234</c>；这里剥掉 <c>+Sha</c> 后缀，得到可显示的 semver。
/// 开发态未注入时回落到 csproj 的 <c>&lt;Version&gt;</c>。
/// </summary>
public static class AppInfo
{
    /// <summary>本项目仓库（owner/name）——更新检查必须指向它，而不是上游的 Tauri 实现。</summary>
    public const string Repo = "lytree/OpenList.Net";

    public const string RepoUrl = "https://github.com/lytree/OpenList.Net";

    public const string ReleasesApi = "https://api.github.com/repos/lytree/OpenList.Net/releases/latest";

    public const string ReleasesPage = "https://github.com/lytree/OpenList.Net/releases";

    /// <summary>形如 "4.2.6"。</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>形如 "v4.2.6"，与 GitHub release 的 tag 直接可比。</summary>
    public static string Tag => "v" + Version;

    private static string ResolveVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            var semver = plus > 0 ? info[..plus] : info;
            if (!string.IsNullOrWhiteSpace(semver)) return semver;
        }

        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
