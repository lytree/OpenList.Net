using OpenList.Core.Drivers;
using OpenList.Core.Models;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace OpenList.Infrastructure.Services;

public interface IDriverFactory
{
    void LoadDriver(Storage storage);
    void UnloadDriver(string mountPath);
    IDriver? GetDriver(string mountPath);
    DriverInfo GetDriverInfo(string driverName);
    List<DriverInfo> ListDriverInfos();
    List<string> ListDriverNames();
    Storage? FindStorageByPath(string path);
    List<Storage> GetAllStorages();
    /// <summary>
    /// Get virtual directory entries under the given path (Go: getStorageVirtualFilesByPath).
    /// Returns mount points that are direct children of the given prefix path.
    /// </summary>
    List<ObjItem> GetVirtualFilesByPath(string prefix);
}

/// <summary>
/// Mirrors Go's internal/op/driver.go: RegisterDriver, GetDriver, GetDriverInfoMap, etc.
/// On construction, scans assemblies for IDriver implementations, creates a temporary
/// instance per driver to call Info() once, and caches both the factory and the DriverInfo.
/// </summary>
public class DriverFactory : IDriverFactory
{
    private readonly ConcurrentDictionary<string, IDriver> _drivers = new();
    private readonly ConcurrentDictionary<string, Func<IDriver>> _driverFactories = new();
    private readonly ConcurrentDictionary<string, DriverInfo> _driverInfoMap = new();
    private readonly ILogger<DriverFactory>? _logger;

    public DriverFactory(ILogger<DriverFactory>? logger = null)
    {
        _logger = logger;
        RegisterDrivers();
    }

    private void RegisterDrivers()
    {
        // Ensure the Drivers assembly is loaded
        try { Assembly.Load("OpenList.Drivers"); } catch { }

        // Auto-discover all IDriver implementations via reflection
        var driverType = typeof(IDriver);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("OpenList") == true);

        foreach (var assembly in assemblies)
        {
            IEnumerable<Type> types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null)!; }
            catch { continue; }

            foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false } && driverType.IsAssignableFrom(t)))
            {
                try
                {
                    // Create a temporary instance to get Config and Info — mirrors Go's RegisterDriver
                    var tempInstance = (IDriver)Activator.CreateInstance(type)!;
                    var config = tempInstance.Config();
                    var key = !string.IsNullOrEmpty(config.Name) ? config.Name : type.Name;

                    _driverFactories[key] = () => (IDriver)Activator.CreateInstance(type)!;

                    // Pre-generate and cache DriverInfo — mirrors Go's registerDriverItems
                    _driverInfoMap[key] = tempInstance.Info();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to register driver type {Type}", type.Name);
                }
            }
        }
    }

    public void LoadDriver(Storage storage)
    {
        if (!_driverFactories.TryGetValue(storage.Driver, out var factory))
        {
            _logger?.LogWarning("Driver '{Driver}' not found for storage '{MountPath}'", storage.Driver, storage.MountPath);
            return;
        }
        var driver = factory();
        driver.SetStorage(storage);
        try
        {
            driver.InitAsync().GetAwaiter().GetResult();
            _drivers[storage.MountPath] = driver;
            _logger?.LogInformation("Loaded driver '{Driver}' at mount path '{MountPath}'", storage.Driver, storage.MountPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize driver '{Driver}' for storage '{MountPath}'", storage.Driver, storage.MountPath);
        }
    }

    public void UnloadDriver(string mountPath)
    {
        if (_drivers.TryRemove(mountPath, out var driver))
        {
            try { driver.DropAsync().GetAwaiter().GetResult(); } catch { }
        }
    }

    public IDriver? GetDriver(string mountPath) => _drivers.GetValueOrDefault(mountPath);

    /// <summary>Returns cached DriverInfo — mirrors Go's GetDriverInfoMap</summary>
    public DriverInfo GetDriverInfo(string driverName)
        => _driverInfoMap.GetValueOrDefault(driverName) ?? new DriverInfo();

    /// <summary>Returns all cached DriverInfos — mirrors Go's GetDriverInfoMap</summary>
    public List<DriverInfo> ListDriverInfos() => [.. _driverInfoMap.Values];

    public List<string> ListDriverNames() => [.. _driverInfoMap.Keys.OrderBy(k => k)];

    public Storage? FindStorageByPath(string path)
    {
        // Match like Go's IsSubPath: path == mountPath || path starts with mountPath + "/"
        var match = _drivers
            .Where(kvp => IsSubPath(kvp.Key, path))
            .OrderByDescending(kvp => kvp.Key.Length)
            .FirstOrDefault();
        return match.Value?.GetStorage();
    }

    /// <summary>
    /// Checks if path equals mountPath or is a sub-path of mountPath.
    /// Mirrors Go's utils.IsSubPath: path == subPath || HasPrefix(subPath, path+"/")
    /// </summary>
    private static bool IsSubPath(string mountPath, string path)
    {
        if (string.Equals(mountPath, path, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = mountPath.EndsWith('/') ? mountPath : mountPath + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public List<Storage> GetAllStorages()
        => _drivers.Values.Select(d => d.GetStorage()).Where(s => s != null).ToList()!;

    /// <summary>
    /// Get virtual directory entries under the given prefix path.
    /// Mirrors Go's getStorageVirtualFilesByPath: for each storage whose mount path
    /// starts with prefix+"/", extract the next path segment as a virtual directory name.
    /// </summary>
    public List<ObjItem> GetVirtualFilesByPath(string prefix)
    {
        var files = new List<ObjItem>();
        var seen = new Dictionary<string, int>();

        // Normalize prefix: ensure it ends with /
        if (!prefix.EndsWith('/')) prefix += "/";

        var storages = _drivers.Values
            .Select(d => d.GetStorage())
            .Where(s => s != null && !s.Disabled)
            .OrderBy(s => s.Order)
            .ThenBy(s => s.MountPath)
            .ToList();

        foreach (var storage in storages)
        {
            var mountPath = storage.MountPath;
            // Normalize mount path
            if (!mountPath.StartsWith('/')) mountPath = "/" + mountPath;

            // Check if mountPath starts with prefix
            if (!mountPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = mountPath[prefix.Length..];
            if (string.IsNullOrEmpty(relative)) continue; // Skip exact match (the prefix itself)

            // Get the first path segment after prefix
            var name = relative.Split('/')[0];
            var hasMore = relative.Length > name.Length; // e.g., prefix="/a", mountPath="/a/b/c" → name="b", hasMore=true

            if (seen.TryGetValue(name, out var idx))
            {
                // Duplicate name: if this is a direct mount (no more segments), mark as Locked|Virtual
                if (!hasMore)
                {
                    files[idx].Mask = ObjMask.Locked | ObjMask.Virtual;
                }
                continue;
            }

            seen[name] = files.Count;
            var obj = new ObjItem
            {
                Name = name,
                IsFolder = true,
                Modified = storage.Modified,
                Mask = hasMore ? (ObjMask.ReadOnly | ObjMask.Virtual) : (ObjMask.Locked | ObjMask.Virtual)
            };
            files.Add(obj);
        }

        return files;
    }
}
