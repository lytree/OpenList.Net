using System.Collections.Concurrent;
using System.Reflection;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;
using Zio.FileSystems;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Auto-discovers <see cref="ZioDriverBase"/> subclasses from assemblies starting with "OpenList"
/// and mounts them into a Zio <see cref="MountFileSystem"/>. Adding a driver = create a class in
/// OpenList.Drivers — no manual registration needed.
/// Mirrors OpenList Go <c>internal/op</c> storage loading.
/// </summary>
public sealed class ZioMountService : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ZioDriverBase> _drivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ZioDriverBase> _mounted = new(StringComparer.OrdinalIgnoreCase);
    private readonly MountFileSystem _root = new();

    /// <summary>Root mount filesystem exposing all enabled storages under their mount paths.</summary>
    public MountFileSystem Root => _root;

    /// <summary>Discovered driver type name → type mapping (lazy populated).</summary>
    public IReadOnlyDictionary<string, Type> DriverTypes => DiscoverDriverTypes()
        .ToDictionary(t => ((ZioDriverBase)Activator.CreateInstance(t)!).DriverName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Currently mounted drivers keyed by mount path.</summary>
    public IReadOnlyDictionary<string, ZioDriverBase> Mounted => _mounted;

    /// <summary>
    /// Ensures the <c>OpenList.Drivers</c> assembly is loaded into the AppDomain.
    /// The assembly is referenced only at the project level (no direct type usage in
    /// Server/Infrastructure), so the CLR won't load it on demand — we must load it
    /// explicitly before scanning for <see cref="ZioDriverBase"/> subclasses.
    /// </summary>
    private static void EnsureDriverAssembliesLoaded()
    {
        var loaded = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        foreach (var name in new[] { "OpenList.Drivers", "OpenList.Core" })
        {
            if (loaded.Contains(name)) continue;
            try { AppDomain.CurrentDomain.Load(new AssemblyName(name)); }
            catch { /* assembly may not be present in this host */ }
        }
    }

    public IEnumerable<Type> DiscoverDriverTypes()
    {
        EnsureDriverAssembliesLoaded();
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("OpenList", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ZioDriverBase).IsAssignableFrom(t));
    }

    public string GetDriverName(string driverType)
    {
        if (DriverTypes.TryGetValue(driverType, out var t))
            return ((ZioDriverBase)Activator.CreateInstance(t)!).DriverName;
        return driverType;
    }

    /// <summary>Load all enabled storages from the database.</summary>
    public Task LoadAllAsync(IEnumerable<Storage> storages, CancellationToken ct = default)
    {
        foreach (var s in storages.Where(s => !s.Disabled).OrderBy(s => s.Order))
        {
            LoadAsync(s, ct).GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }

    /// <summary>Instantiate a driver, call InitAsync, and mount it at <paramref name="storage"/>'s mount path.</summary>
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

    /// <summary>Unmount and dispose the driver at <paramref name="mountPath"/>.</summary>
    public Task UnloadAsync(string mountPath, CancellationToken ct = default)
    {
        if (_mounted.TryRemove(mountPath, out var driver))
        {
            try { _root.Unmount(NormalizeMountPath(mountPath)); } catch { }
            driver.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }

    /// <summary>Resolve a driver by its type name (short name or full name).</summary>
    public Type? ResolveDriverType(string driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName)) return null;
        // Match by short type name (e.g. "LocalDriver"), DriverName (e.g. "Local"), or full name.
        var types = DiscoverDriverTypes().ToList();
        return types.FirstOrDefault(t => t.Name.Equals(driverName, StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault(t => ((ZioDriverBase)Activator.CreateInstance(t)!).DriverName.Equals(driverName, StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault(t => t.FullName?.Equals(driverName, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>Find the driver mounted at or above <paramref name="path"/>.</summary>
    public ZioDriverBase? FindDriverForPath(string path)
    {
        var norm = NormalizeMountPath(path).FullName;
        var best = _mounted.FirstOrDefault(kvp => norm.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase));
        return best.Value;
    }

    private static UPath NormalizeMountPath(string mountPath)
    {
        if (string.IsNullOrWhiteSpace(mountPath)) return UPath.Root;
        if (!mountPath.StartsWith('/')) mountPath = "/" + mountPath;
        return new UPath(mountPath);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _mounted)
        {
            try { await kvp.Value.DisposeAsync(); } catch { }
        }
        _mounted.Clear();
        _root.Dispose();
    }

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
