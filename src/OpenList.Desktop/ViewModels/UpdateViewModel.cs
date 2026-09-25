using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>
/// 更新页（上游 UpdateView/UpdateManagerCard 复刻）：
/// 当前版本 tag + 检查更新 + 自动检查更新开关。
/// </summary>
public partial class UpdateViewModel : ViewModelBase
{
    private readonly UpdateChecker _updater;
    private readonly AppSettings _settings;

    [ObservableProperty] private string _currentVersion = "v0.1.0";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _autoCheckUpdate;
    [ObservableProperty] private VersionEntry? _selectedVersion;

    public ObservableCollection<VersionEntry> AvailableVersions { get; } = [];

    public UpdateViewModel(UpdateChecker updater, AppSettings settings, LogEventBus log)
    {
        _updater = updater;
        _settings = settings;
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
        StatusText = "检查中...";
        try
        {
            var releases = await _updater.ListReleasesAsync(take: 10);
            AvailableVersions.Clear();
            foreach (var r in releases)
                AvailableVersions.Add(new VersionEntry(r.Tag, r.Url));
            var info = releases.Count > 0 ? releases[0] : null;
            StatusText = info is null
                ? "没有可用更新"
                : info.Tag == CurrentVersion
                    ? "应用已是最新版本"
                    : $"发现新版本 {info.Tag}";
        }
        catch (Exception ex)
        {
            StatusText = $"检查更新失败: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void DownloadAndInstall()
    {
        if (SelectedVersion is null) return;
        UpdateChecker.OpenUrl(SelectedVersion.Url);
        StatusText = "已打开发布页，请下载最新安装包";
    }
}
