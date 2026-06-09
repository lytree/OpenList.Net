using OpenList.Core.Drivers;
using OpenList.Core.Models;
using System.Collections.Concurrent;
using System.Reflection;

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

    public DriverFactory()
    {
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
                catch { }
            }
        }
    }

    public void LoadDriver(Storage storage)
    {
        if (!_driverFactories.TryGetValue(storage.Driver, out var factory)) return;
        var driver = factory();
        driver.SetStorage(storage);
        try
        {
            driver.InitAsync().GetAwaiter().GetResult();
            _drivers[storage.MountPath] = driver;
        }
        catch
        {
            // Log error but don't crash
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
        var match = _drivers
            .Where(kvp => path.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kvp => kvp.Key.Length)
            .FirstOrDefault();
        return match.Value?.GetStorage();
    }

    public List<Storage> GetAllStorages()
        => _drivers.Values.Select(d => d.GetStorage()).Where(s => s != null).ToList()!;
}
