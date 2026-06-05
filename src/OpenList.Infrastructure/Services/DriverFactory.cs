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
}

public class DriverFactory : IDriverFactory
{
    private readonly ConcurrentDictionary<string, IDriver> _drivers = new();
    private readonly ConcurrentDictionary<string, Func<IDriver>> _driverFactories = new();

    public DriverFactory()
    {
        RegisterDrivers();
    }

    private void RegisterDrivers()
    {
        // Auto-discover all IDriver implementations via reflection
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("OpenList") == true);

        var driverType = typeof(IDriver);
        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => t is { IsClass: true, IsAbstract: false } && driverType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        var instance = (IDriver)Activator.CreateInstance(type)!;
                        var config = instance.Config();
                        var key = type.Name.ToLowerInvariant();
                        // Use config name as key if available
                        if (!string.IsNullOrEmpty(config.Name))
                            key = ToSnakeCase(config.Name);

                        _driverFactories[key] = () => (IDriver)Activator.CreateInstance(type)!;
                    }
                    catch { }
                }
            }
            catch { }
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

    public DriverInfo GetDriverInfo(string driverName)
    {
        if (_driverFactories.TryGetValue(driverName, out var factory))
        {
            var d = factory();
            return new DriverInfo { Config = d.Config() };
        }
        return new DriverInfo();
    }

    public List<DriverInfo> ListDriverInfos()
        => _driverFactories.Values.Select(f =>
        {
            var d = f();
            return new DriverInfo { Config = d.Config() };
        }).ToList();

    public List<string> ListDriverNames() => [.. _driverFactories.Keys.OrderBy(k => k)];

    public Storage? FindStorageByPath(string path)
    {
        var match = _drivers
            .Where(kvp => path.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kvp => kvp.Key.Length)
            .FirstOrDefault();
        return match.Value?.GetStorage();
    }

    private static string ToSnakeCase(string name)
    {
        return string.Concat(name.Select((c, i) =>
            i > 0 && (char.IsUpper(c) || char.IsDigit(c)) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()))
            .Replace("__", "_");
    }
}
