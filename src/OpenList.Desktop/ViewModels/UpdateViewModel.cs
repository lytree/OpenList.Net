using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Localization;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>
/// 更新页（上游 UpdateView/UpdateManagerCard 复刻）：
/// 当前版本 tag + 检查更新 + 自动检查更新开关。
///
/// <para>检查的是<b>本项目</b>（<see cref="AppInfo.Repo"/>）的 release，
/// 当前版本号来自程序集而非写死的字符串——否则会拿别人的版本号与自己比，
/// 每次检查都误报有更新。</para>
/// </summary>
public partial class UpdateViewModel : ViewModelBase
{
    private readonly UpdateChecker _updater;
    private readonly AppSettings _settings;
    private readonly LogEventBus _log;

    /// <summary>当前版本 tag，如 "v4.2.6"（来自程序集，见 <see cref="AppInfo"/>）。</summary>
    public string CurrentVersion { get; } = AppInfo.Tag;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _autoCheckUpdate;
    [ObservableProperty] private VersionEntry? _selectedVersion;

    public ObservableCollection<VersionEntry> AvailableVersions { get; } = [];

    public UpdateViewModel(UpdateChecker updater, AppSettings settings, LogEventBus log)
    {
        _updater = updater;
        _settings = settings;
        _log = log;
        _autoCheckUpdate = settings.AutoCheckUpdate;
    }

    partial void OnAutoCheckUpdateChanged(bool value)
    {
        _settings.AutoCheckUpdate = value;
        _settings.Save();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        IsChecking = true;
        StatusText = Localizer.Get("upd.checking");
        try
        {
            var releases = await _updater.ListReleasesAsync(take: 10);
            AvailableVersions.Clear();
            foreach (var r in releases)
                AvailableVersions.Add(new VersionEntry(r.Tag, r.Url));

            var latest = releases.Count > 0 ? releases[0] : null;
            StatusText = latest is null
                ? Localizer.Get("upd.none")
                : IsNewer(latest.Tag, CurrentVersion)
                    ? Localizer.Format("upd.found", latest.Tag)
                    : Localizer.Get("upd.upToDate");
        }
        catch (Exception ex)
        {
            StatusText = Localizer.Format("upd.checkFailed", ex.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>远端 tag 是否比本地版本新；无法解析为版本号时退化为字符串比较。</summary>
    private static bool IsNewer(string remoteTag, string localTag)
    {
        var remote = ParseVersion(remoteTag);
        var local = ParseVersion(localTag);
        if (remote is not null && local is not null) return remote > local;
        return !string.Equals(remoteTag, localTag, StringComparison.OrdinalIgnoreCase);
    }

    private static Version? ParseVersion(string tag)
    {
        var trimmed = tag.TrimStart('v', 'V');
        // 去掉预发布后缀（-beta.1 之类），只比数字部分
        var dash = trimmed.IndexOf('-');
        if (dash > 0) trimmed = trimmed[..dash];
        return Version.TryParse(trimmed, out var v) ? v : null;
    }

    [RelayCommand]
    private void DownloadAndInstall()
    {
        if (SelectedVersion is null) return;
        UpdateChecker.OpenUrl(SelectedVersion.Url);
        StatusText = Localizer.Get("upd.opened");
    }
}
