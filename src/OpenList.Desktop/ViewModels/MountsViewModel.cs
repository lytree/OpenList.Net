using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenList.Desktop.Services;

namespace OpenList.Desktop.ViewModels;

/// <summary>挂载列表里的一行：配置 + 运行状态。</summary>
public partial class MountItemViewModel : ObservableObject
{
    public MountConfig Config { get; }

    public MountItemViewModel(MountConfig config) => Config = config;

    public string Name => Config.Name;
    public string WebDavUrl => Config.WebDavUrl;
    public string MountPoint => Config.MountPoint;
    public bool AutoMount => Config.AutoMount;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isEditing;

    public string StatusText => IsRunning ? "已挂载" : "未挂载";

    // 编辑态字段（直接绑）
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editUrl = "";
    [ObservableProperty] private string _editUsername = "";
    [ObservableProperty] private string _editPassword = "";
    [ObservableProperty] private string _editMountPoint = "";
    [ObservableProperty] private string _editExtraFlags = "";
    [ObservableProperty] private bool _editAutoMount;

    public MountItemViewModel() : this(new MountConfig())
    {
        IsEditing = true;
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnEditNameChanged(string value) => Config.Name = value;
    partial void OnEditUrlChanged(string value) => Config.WebDavUrl = value;
    partial void OnEditUsernameChanged(string value) => Config.Username = value;
    partial void OnEditPasswordChanged(string value) => Config.Password = value;
    partial void OnEditMountPointChanged(string value) => Config.MountPoint = value;
    partial void OnEditExtraFlagsChanged(string value) => Config.ExtraFlags = value;
    partial void OnEditAutoMountChanged(bool value) => Config.AutoMount = value;
}

/// <summary>挂载管理页（上游 MountView 复刻）：头卡 + WinFSP 提示 + 搜索筛选 + 空态/挂载卡。</summary>
public partial class MountsViewModel : ViewModelBase
{
    private readonly RcloneService _rclone;
    private readonly AppSettings _settings;
    private readonly LogEventBus _log;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _statusFilterIndex; // 0=所有状态 1=已挂载 2=未挂载
    [ObservableProperty] private bool _winFspInstalled;
    [ObservableProperty] private bool _winFspTipVisible;

    public ObservableCollection<MountItemViewModel> Items { get; } = [];

    /// <summary>筛选后的列表（搜索框 + 状态下拉），上游 filters 行为。</summary>
    public ObservableCollection<MountItemViewModel> FilteredItems { get; } = [];

    public string MountedStats => $"{Items.Count(i => i.IsRunning)} / {Items.Count} 已挂载";
    public bool HasNoVisibleItem => FilteredItems.Count == 0;

    public MountsViewModel(RcloneService rclone, AppSettings settings, LogEventBus log)
    {
        _rclone = rclone;
        _settings = settings;
        _log = log;
        _rclone.Changed += () => Dispatcher.UIThread.Post(RefreshStatus);
        foreach (var m in settings.Mounts)
            Items.Add(new MountItemViewModel(Clone(m)) { IsRunning = _rclone.IsRunning(m.Name) });
        Items.CollectionChanged += (_, _) => ApplyFilter();
        foreach (var item in Items)
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MountItemViewModel.IsRunning)) OnRunningChanged(); };
        WinFspInstalled = DetectWinFsp();
        WinFspTipVisible = !WinFspInstalled && !settings.WinFspTipDismissed;
        ApplyFilter();
    }

    private void OnRunningChanged()
    {
        OnPropertyChanged(nameof(MountedStats));
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnStatusFilterIndexChanged(int value) => ApplyFilter();

    private void ApplyFilter()
    {
        var kw = SearchText.Trim();
        IEnumerable<MountItemViewModel> q = Items;
        if (kw.Length > 0)
            q = q.Where(i => i.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                             || i.MountPoint.Contains(kw, StringComparison.OrdinalIgnoreCase));
        q = StatusFilterIndex switch
        {
            1 => q.Where(i => i.IsRunning),
            2 => q.Where(i => !i.IsRunning),
            _ => q,
        };
        FilteredItems.Clear();
        foreach (var it in q)
            FilteredItems.Add(it);
        OnPropertyChanged(nameof(MountedStats));
        OnPropertyChanged(nameof(HasNoVisibleItem));
    }

    private static bool DetectWinFsp()
    {
        if (!OperatingSystem.IsWindows()) return true; // 非 Windows 不提示
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return File.Exists(Path.Combine(pf, "WinFsp", "bin", "winfsp-x64.dll"))
                || File.Exists(Path.Combine(pf, "WinFsp", "bin", "winfsp-arm64.dll"));
        }
        catch { return false; }
    }

    [RelayCommand]
    private void DismissWinFspTip()
    {
        WinFspTipVisible = false;
        _settings.WinFspTipDismissed = true;
        _settings.Save();
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        foreach (var item in Items)
            item.IsRunning = _rclone.IsRunning(item.Name);
        StatusText = $"刷新于 {DateTime.Now:HH:mm:ss}";
    }

    private static MountConfig Clone(MountConfig m) => new()
    {
        Name = m.Name, WebDavUrl = m.WebDavUrl, Username = m.Username,
        Password = m.Password, MountPoint = m.MountPoint, ExtraFlags = m.ExtraFlags,
        AutoMount = m.AutoMount,
    };

    [RelayCommand]
    private void Add()
    {
        var item = new MountItemViewModel
        {
            EditName = $"remote{Items.Count + 1}",
            EditUrl = "http://127.0.0.1:5244/dav",
            EditMountPoint = OperatingSystem.IsWindows() ? "X:" : "/mnt/openlist",
        };
        Items.Add(item);
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MountItemViewModel.IsRunning)) OnRunningChanged(); };
        ApplyFilter();
    }

    [RelayCommand]
    private void Edit(MountItemViewModel? item)
    {
        if (item is null) return;
        item.EditName = item.Config.Name;
        item.EditUrl = item.Config.WebDavUrl;
        item.EditUsername = item.Config.Username;
        item.EditPassword = item.Config.Password;
        item.EditMountPoint = item.Config.MountPoint;
        item.EditExtraFlags = item.Config.ExtraFlags;
        item.EditAutoMount = item.Config.AutoMount;
        item.IsEditing = true;
    }

    [RelayCommand]
    private void Remove(MountItemViewModel? item)
    {
        if (item is null) return;
        if (item.IsRunning) _ = _rclone.StopAsync(item.Name);
        Items.Remove(item);
        Persist();
    }

    [RelayCommand]
    private async Task SaveAsync(MountItemViewModel? item)
    {
        if (item is null) return;
        if (string.IsNullOrWhiteSpace(item.EditName)) { StatusText = "名称不能为空"; return; }
        var dup = Items.FirstOrDefault(i => i != item && i.Name == item.EditName);
        if (dup is not null) { StatusText = $"名称 {item.EditName} 已存在"; return; }

        var wasRunning = item.IsRunning;
        if (wasRunning) await _rclone.StopAsync(item.Config.Name);

        item.IsEditing = false;
        item.IsRunning = false;
        OnPropertyChanged(nameof(Items));
        Persist();
        StatusText = $"已保存 {item.Name}";
        if (wasRunning) await MountAsync(item);
    }

    [RelayCommand]
    private async Task MountAsync(MountItemViewModel? item)
    {
        if (item is null) return;
        if (item.IsEditing) { StatusText = "请先保存"; return; }
        StatusText = $"挂载中: {item.Name}…";
        var ok = await _rclone.StartAsync(item.Config);
        item.IsRunning = _rclone.IsRunning(item.Name);
        StatusText = ok ? $"已挂载: {item.Name} → {item.MountPoint}" : $"挂载失败: {item.Name}";
    }

    [RelayCommand]
    private async Task UnmountAsync(MountItemViewModel? item)
    {
        if (item is null) return;
        await _rclone.StopAsync(item.Name);
        item.IsRunning = false;
        StatusText = $"已卸载: {item.Name}";
    }

    private void Persist()
    {
        _settings.Mounts = [.. Items.Select(i => i.Config)];
        _settings.Save();
    }
}
