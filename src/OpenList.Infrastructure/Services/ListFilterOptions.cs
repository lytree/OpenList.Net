using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 文件列表过滤选项（<c>hide_files</c> / <c>ignore_system_files</c>）的只读快照。
/// <para>
/// 和缩略图配置同样的思路：<c>fs/list</c> 是热路径，逐请求查设置表不值得；
/// 由 <see cref="ListFilterOptionsProvider"/> 每 20 秒后台重读一次。
/// </para>
/// </summary>
public sealed record ListFilterOptions
{
    /// <summary>是否隐藏操作系统产生的元数据文件（<c>.DS_Store</c> / <c>Thumbs.db</c> 等）。</summary>
    public bool IgnoreSystemFiles { get; init; }

    /// <summary>隐藏规则：设置为每行一个正则，匹配文件名（不是完整路径）。</summary>
    public IReadOnlyList<Regex> HideFilePatterns { get; init; } = [];

    /// <summary>是否应把该条目从列表中隐藏。</summary>
    public bool ShouldHide(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (IgnoreSystemFiles && IsSystemFile(name)) return true;
        foreach (var pattern in HideFilePatterns)
        {
            try
            {
                if (pattern.IsMatch(name)) return true;
            }
            catch (RegexMatchTimeoutException) { /* 病态正则只跳过该条，不影响整页列表 */ }
        }
        return false;
    }

    /// <summary>常见的系统/隐藏元数据文件（大小写不敏感）。</summary>
    private static bool IsSystemFile(string name)
    {
        if (name is "." or "..") return false;
        // 以点开头的隐藏文件与目录（Linux/macOS 惯例）
        if (name.StartsWith('.')) return true;
        return name.ToLowerInvariant() switch
        {
            "thumbs.db" => true,
            "desktop.ini" => true,
            "ehthumbs.db" => true,
            _ => false,
        };
    }

    /// <summary>从设置字典构建快照：非法正则忽略并记日志，不抛异常。</summary>
    public static ListFilterOptions FromSettings(
        IReadOnlyDictionary<string, string> settings,
        Action<string>? warnInvalidPattern = null)
    {
        var patterns = new List<Regex>();
        if (settings.TryGetValue("hide_files", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    patterns.Add(new Regex(line, RegexOptions.Compiled | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(200)));
                }
                catch (ArgumentException)
                {
                    // 管理员写了一个不合法的正则：跳过它，别让整个列表接口报错。
                    warnInvalidPattern?.Invoke(line);
                }
            }
        }

        var ignoreSystem = settings.TryGetValue("ignore_system_files", out var flag)
                           && bool.TryParse(flag, out var parsed) && parsed;

        return new ListFilterOptions { IgnoreSystemFiles = ignoreSystem, HideFilePatterns = patterns };
    }

    /// <summary>空过滤（未读到设置前的兜底：什么都不隐藏）。</summary>
    public static ListFilterOptions None { get; } = new();
}

/// <summary>
/// <see cref="ListFilterOptions"/> 快照持有者（单例），启动后每 20 秒重读一次设置表。
/// </summary>
public sealed class ListFilterOptionsProvider : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ListFilterOptionsProvider> _log;
    private readonly CancellationTokenSource _stop = new();
    private volatile ListFilterOptions _current = ListFilterOptions.None;

    /// <summary>创建提供者并启动后台刷新循环。</summary>
    public ListFilterOptionsProvider(IServiceScopeFactory scopes, ILogger<ListFilterOptionsProvider> log)
    {
        _scopes = scopes;
        _log = log;
        _ = Task.Run(RefreshLoopAsync);
    }

    /// <summary>当前快照（永不为 null）。</summary>
    public ListFilterOptions Current => _current;

    /// <summary>立即重读一次设置（供启动流程与测试调用）。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetService<ISettingService>();
            if (settings is null) return;
            var resp = await settings.ListAsync(ct).ConfigureAwait(false);
            if (resp.Code != 200 || resp.Data is null) return;
            var dict = resp.Data.ToDictionary(s => s.Key, s => s.Value ?? string.Empty, StringComparer.Ordinal);
            _current = ListFilterOptions.FromSettings(dict, bad => _log.LogWarning("list filter: invalid hide_files regex '{Pattern}'", bad));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "list filter: refresh options failed");
        }
    }

    private async Task RefreshLoopAsync()
    {
        await RefreshAsync(_stop.Token).ConfigureAwait(false);
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await RefreshAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 进程退出 */ }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
