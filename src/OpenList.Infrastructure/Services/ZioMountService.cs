using System.Collections.Concurrent;
using System.Reflection;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;
using Zio.FileSystems;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 自动发现以 “OpenList” 开头的程序集中的 <see cref="ZioDriverBase"/> 子类，
/// 并把它们挂载到 Zio 的 <see cref="MountFileSystem"/> 中。新增驱动 =
/// 在 OpenList.Drivers 中创建一个类 —— 无需手动注册。
/// 对应 Go 参考实现 <c>internal/op</c> 的存储加载逻辑。
/// </summary>
public sealed class ZioMountService : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ZioDriverBase> _drivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ZioDriverBase> _mounted = new(StringComparer.OrdinalIgnoreCase);
    private readonly MountFileSystem _root = new();

    /// <summary>根挂载文件系统，按挂载路径暴露所有已启用的存储。</summary>
    public MountFileSystem Root => _root;

    /// <summary>已发现的驱动类型名 → 类型映射（惰性填充）。</summary>
    public IReadOnlyDictionary<string, Type> DriverTypes => DiscoverDriverTypes()
        .ToDictionary(t => ((ZioDriverBase)Activator.CreateInstance(t)!).DriverName, StringComparer.OrdinalIgnoreCase);

    /// <summary>当前已挂载的驱动，按挂载路径索引。</summary>
    public IReadOnlyDictionary<string, ZioDriverBase> Mounted => _mounted;

    /// <summary>
    /// 确保 <c>OpenList.Drivers</c> 程序集被加载到 AppDomain。该程序集仅在项目层面被引用
    /// （Server/Infrastructure 中没有直接使用其类型），因此 CLR 不会按需加载它 —— 在扫描
    /// <see cref="ZioDriverBase"/> 子类之前必须先显式加载。
    /// </summary>
    private static void EnsureDriverAssembliesLoaded()
    {
        var loaded = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        foreach (var name in new[] { "OpenList.Drivers", "OpenList.Core" })
        {
            if (loaded.Contains(name)) continue;
            try { AppDomain.CurrentDomain.Load(new AssemblyName(name)); }
            catch { /* 该宿主中可能不存在此程序集 */ }
        }
    }

    /// <summary>
    /// 发现所有 <see cref="ZioDriverBase"/> 的具体子类。
    /// </summary>
    public IEnumerable<Type> DiscoverDriverTypes()
    {
        EnsureDriverAssembliesLoaded();
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("OpenList", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ZioDriverBase).IsAssignableFrom(t));
    }

    /// <summary>
    /// 取给定驱动类型名的驱动名称（例如传入 <c>LocalDriver</c> 得到 <c>Local</c>）。
    /// </summary>
    public string GetDriverName(string driverType)
    {
        if (DriverTypes.TryGetValue(driverType, out var t))
            return ((ZioDriverBase)Activator.CreateInstance(t)!).DriverName;
        return driverType;
    }

    /// <summary>从数据库加载所有已启用的存储。</summary>
    public Task LoadAllAsync(IEnumerable<Storage> storages, CancellationToken ct = default)
    {
        foreach (var s in storages.Where(s => !s.Disabled).OrderBy(s => s.Order))
        {
            LoadAsync(s, ct).GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 实例化驱动、调用 InitAsync，并按 <paramref name="storage"/> 的挂载路径挂载它。
    /// </summary>
    public async Task LoadAsync(Storage storage, CancellationToken ct = default)
    {
        if (_mounted.ContainsKey(storage.MountPath)) await UnloadAsync(storage.MountPath, ct);

        var type = ResolveDriverType(storage.Driver);
        if (type is null) return;

        var driver = (ZioDriverBase)Activator.CreateInstance(type)!;
        await driver.InitAsync(DriverConfig.FromStorage(storage), ct);
        if (driver.System is null) return;

        var mountPath = NormalizeMountPath(storage.MountPath);
        _root.Mount(mountPath, driver.System);
        _mounted[storage.MountPath] = driver;
    }

    /// <summary>卸载并释放 <paramref name="mountPath"/> 处的驱动。</summary>
    public Task UnloadAsync(string mountPath, CancellationToken ct = default)
    {
        if (_mounted.TryRemove(mountPath, out var driver))
        {
            try { _root.Unmount(NormalizeMountPath(mountPath)); } catch { }
            driver.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }

    /// <summary>按驱动类型名（短名或全名）解析对应驱动类型。</summary>
    public Type? ResolveDriverType(string driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName)) return null;
        // 依次匹配短类型名（如 "LocalDriver"）、驱动名（如 "Local"）、完整名称。
        var types = DiscoverDriverTypes().ToList();
        return types.FirstOrDefault(t => t.Name.Equals(driverName, StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault(t => ((ZioDriverBase)Activator.CreateInstance(t)!).DriverName.Equals(driverName, StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault(t => t.FullName?.Equals(driverName, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>查找挂在 <paramref name="path"/> 处或其上层任意祖先上的驱动。</summary>
    public ZioDriverBase? FindDriverForPath(string path)
    {
        var norm = NormalizeMountPath(path).FullName;
        var best = _mounted.FirstOrDefault(kvp => norm.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase));
        return best.Value;
    }

    /// <summary>
    /// 把完整挂载路径（如 <c>/test/a.zip</c>）转换为相对所匹配驱动文件系统的路径（如 <c>/a.zip</c>）。
    /// 驱动的 <see cref="ZioDriverBase.System"/> 是以其挂载点为根的子文件系统，因此驱动级操作
    /// （归档列举/解压）必须使用相对挂载点的路径，而非完整挂载路径。无匹配挂载时返回原路径。
    /// </summary>
    public string ToDriverRelativePath(string path)
    {
        var norm = NormalizeMountPath(path).FullName;
        var key = _mounted
            .Where(kvp => norm.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kvp => kvp.Key.Length)
            .Select(kvp => kvp.Key)
            .FirstOrDefault();
        if (key is null) return path;
        var rel = norm[key.Length..];
        if (string.IsNullOrEmpty(rel)) return "/";
        return "/" + rel.TrimStart('/');
    }

    /// <summary>把挂载路径规整为 Zio 的 UPath（补上前导 /）。</summary>
    private static UPath NormalizeMountPath(string mountPath)
    {
        if (string.IsNullOrWhiteSpace(mountPath)) return UPath.Root;
        if (!mountPath.StartsWith('/')) mountPath = "/" + mountPath;
        return new UPath(mountPath);
    }

    /// <summary>安全地获取程序集类型，反射加载失败时返回可用的部分类型。</summary>
    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    /// <summary>释放所有已挂载驱动与根文件系统（异步）。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _mounted)
        {
            try { await kvp.Value.DisposeAsync(); } catch { }
        }
        _mounted.Clear();
        _root.Dispose();
    }

    /// <summary>释放所有已挂载驱动与根文件系统。</summary>
    public void Dispose()
    {
        foreach (var kvp in _mounted)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _mounted.Clear();
        _root.Dispose();
    }
}