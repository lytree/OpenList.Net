using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;
using Zio.FileSystems;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Zio-based mount service that manages filesystem drivers using MountFileSystem.
/// Handles driver discovery, loading, and path resolution.
/// </summary>
public class ZioMountService : IDisposable
{
    private readonly MountFileSystem _mountFs;
    private readonly ConcurrentDictionary<string, IFileSystem> _drivers = new();
    private readonly ConcurrentDictionary<string, Storage> _storages = new();
    private readonly ConcurrentDictionary<string, Func<ZioDriverBase>> _driverFactories = new();
    private readonly ConcurrentDictionary<string, DriverInfo> _driverInfoMap = new();
    private readonly ConcurrentDictionary<string, int> _balanceMap = new();
    private readonly ILogger<ZioMountService>? _logger;
    private readonly List<Action<string, IFileSystem>> _storageHooks = [];

    public const string StatusWork = "work";
    public const string StatusDisabled = "disabled";

    public ZioMountService(ILogger<ZioMountService>? logger = null)
    {
        _logger = logger;
        _mountFs = new MountFileSystem(owned: false);
        RegisterDrivers();
    }

    #region Driver Discovery

    private void RegisterDrivers()
    {
        // Ensure the Drivers assembly is loaded
        try { Assembly.Load("OpenList.Drivers"); } catch { }

        // Auto-discover all ZioDriverBase implementations via reflection
        var baseType = typeof(ZioDriverBase);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("OpenList") == true);

        foreach (var assembly in assemblies)
        {
            IEnumerable<Type> types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null)!; }
            catch { continue; }

            foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false } && baseType.IsAssignableFrom(t)))
            {
                try
                {
                    var tempInstance = (ZioDriverBase)Activator.CreateInstance(type)!;
                    var config = tempInstance.Config;
                    var key = !string.IsNullOrEmpty(config.Name) ? config.Name : type.Name;

                    _driverFactories[key] = () => (ZioDriverBase)Activator.CreateInstance(type)!;
                    _driverInfoMap[key] = tempInstance.GetDriverInfo();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to register driver type {Type}", type.Name);
                }
            }
        }
    }

    public void RegisterStorageHook(Action<string, IFileSystem> hook) => _storageHooks.Add(hook);

    private void CallStorageHooks(string type, IFileSystem driver)
    {
        foreach (var hook in _storageHooks)
        {
            try { hook(type, driver); } catch { }
        }
    }

    public DriverInfo GetDriverInfo(string driverName)
        => _driverInfoMap.GetValueOrDefault(driverName) ?? new DriverInfo();

    public List<DriverInfo> ListDriverInfos() => [.. _driverInfoMap.Values];

    public List<string> ListDriverNames() => [.. _driverInfoMap.Keys.OrderBy(k => k)];

    #endregion

    #region Driver Loading / Unloading

    /// <summary>
    /// Initialize and load a driver for the given storage.
    /// Go: initStorage + storagesMap.Store.
    /// </summary>
    public void LoadDriver(Storage storage)
    {
        if (!_driverFactories.TryGetValue(storage.Driver, out var factory))
        {
            _logger?.LogWarning("Driver '{Driver}' not found for storage '{MountPath}'", storage.Driver, storage.MountPath);
            return;
        }
        var driver = factory();
        InitAndStoreDriver(storage, driver);
        Task.Run(() => CallStorageHooks("add", driver));
    }

    /// <summary>
    /// Drop and remove the driver at the given mount path.
    /// Go: storageDriver.Drop + storagesMap.Delete
    /// </summary>
    public void UnloadDriver(string mountPath)
    {
        mountPath = NormalizeMountPath(mountPath);
        if (_drivers.TryRemove(mountPath, out var driver))
        {
            UnmountInternal(mountPath);
            if (driver is IFileSystemDriverMeta meta)
            {
                try { meta.DropAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            }
            _storages.TryRemove(mountPath, out _);
            Task.Run(() => CallStorageHooks("del", driver));
        }
    }

    /// <summary>
    /// Reload a storage driver: drop existing, re-init with new storage config.
    /// Go: Drop + initStorage (in UpdateStorage)
    /// </summary>
    public void ReloadDriver(Storage oldStorage, Storage newStorage)
    {
        if (oldStorage.MountPath != newStorage.MountPath)
        {
            UnloadDriver(oldStorage.MountPath);
        }

        ZioDriverBase? driver = null;
        if (oldStorage.MountPath == newStorage.MountPath)
        {
            var existing = GetFileSystem(newStorage.MountPath);
            driver = existing as ZioDriverBase;
        }

        if (driver == null)
        {
            if (!_driverFactories.TryGetValue(newStorage.Driver, out var factory))
            {
                _logger?.LogWarning("Driver '{Driver}' not found for storage '{MountPath}'", newStorage.Driver, newStorage.MountPath);
                return;
            }
            driver = factory();
        }
        else
        {
            try { driver.DropAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        }

        InitAndStoreDriver(newStorage, driver);
        Task.Run(() => CallStorageHooks("update", driver));
    }

    private static readonly JsonSerializerOptions AdditionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new FlexibleBoolConverter() }
    };

    private void InitAndStoreDriver(Storage storage, ZioDriverBase driver)
    {
        driver.Storage = storage;
        var driverStorage = driver.Storage;

        try
        {
            // Unmarshal addition JSON into driver's addition object
            var addition = driver.Addition;
            if (addition != null && !string.IsNullOrEmpty(driverStorage.Addition) && driverStorage.Addition != "{}")
            {
                try
                {
                    var additionType = addition.GetType();
                    var deserialized = JsonSerializer.Deserialize(driverStorage.Addition, additionType, AdditionJsonOptions);
                    if (deserialized != null)
                    {
                        foreach (var prop in additionType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (prop.CanRead && prop.CanWrite)
                            {
                                var val = prop.GetValue(deserialized);
                                if (val != null) prop.SetValue(addition, val);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to unmarshal addition for '{MountPath}'", driverStorage.MountPath);
                }
            }

            // Init driver
            driver.InitAsync().GetAwaiter().GetResult();
            driverStorage.Status = StatusWork;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize driver '{Driver}' for storage '{MountPath}'", storage.Driver, storage.MountPath);
            driverStorage.Status = ex.Message;
        }

        // Mount into MountFileSystem
        var mountPath = NormalizeMountPath(driverStorage.MountPath);
        lock (_drivers)
        {
            // Unmount existing if any
            if (_drivers.ContainsKey(mountPath))
            {
                UnmountInternal(mountPath);
            }

            _mountFs.Mount(new UPath(mountPath), driver);
            _drivers[mountPath] = driver;
            _storages[mountPath] = driverStorage;
        }

        // Save addition back
        var additionJson = SerializeAddition(driver);
        if (additionJson != null)
        {
            driverStorage.Addition = additionJson;
        }
    }

    public string? SerializeAddition(IFileSystem driver)
    {
        if (driver is not IFileSystemDriverMeta meta) return null;
        try
        {
            var addition = meta.Addition;
            if (addition == null) return null;
            return JsonSerializer.Serialize(addition);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to serialize addition for driver");
            return null;
        }
    }

    #endregion

    #region Path Resolution

    /// <summary>
    /// Resolve a request path to the correct filesystem and the path within that filesystem.
    /// Uses MountFileSystem's TryGetMount for longest-prefix matching.
    /// </summary>
    public (IFileSystem? fs, UPath resolvedPath, Storage? storage) Resolve(string path)
    {
        path = NormalizeMountPath(path);
        var uPath = new UPath(path);

        lock (_drivers)
        {
            if (_mountFs.TryGetMount(uPath, out var mountName, out var mountedFs, out var fsPath))
            {
                var mp = mountName.FullName;
                _storages.TryGetValue(mp, out var storage);
                return (mountedFs, fsPath ?? UPath.Root, storage);
            }
        }

        return (null, UPath.Root, null);
    }

    /// <summary>
    /// Find the storage whose mount path is the longest prefix match for the given path.
    /// </summary>
    public Storage? FindStorageByPath(string path)
    {
        var (_, _, storage) = Resolve(path);
        return storage;
    }

    /// <summary>
    /// Get balanced storage for a path (supports .balance suffix for load balancing).
    /// Go: GetBalancedStorage
    /// </summary>
    public IFileSystem? GetBalancedStorage(string path)
    {
        path = NormalizeMountPath(path);
        var storages = GetStoragesByPath(path);
        return storages.Count switch
        {
            0 => null,
            1 => storages[0],
            _ => RoundRobinBalance(storages)
        };
    }

    private List<IFileSystem> GetStoragesByPath(string path)
    {
        var result = new List<IFileSystem>();
        var curSlashCount = 0;

        foreach (var kvp in _drivers)
        {
            var mountPath = GetActualMountPath(kvp.Key);
            if (!IsSubPath(mountPath, path)) continue;

            var slashCount = mountPath.TrimEnd('/').Split('/').Length;
            if (slashCount > curSlashCount)
            {
                result.Clear();
                curSlashCount = slashCount;
            }
            if (slashCount == curSlashCount)
            {
                result.Add(kvp.Value);
            }
        }

        result.Sort((a, b) =>
        {
            var sa = ((a as IFileSystemDriverMeta)?.Storage?.MountPath) ?? "";
            var sb = ((b as IFileSystemDriverMeta)?.Storage?.MountPath) ?? "";
            return string.Compare(sa, sb, StringComparison.Ordinal);
        });
        return result;
    }

    private IFileSystem RoundRobinBalance(List<IFileSystem> storages)
    {
        var first = storages[0] as IFileSystemDriverMeta;
        var virtualPath = GetActualMountPath(first?.Storage?.MountPath ?? "/");
        var i = _balanceMap.GetOrAdd(virtualPath, 0);
        i = (i + 1) % storages.Count;
        _balanceMap[virtualPath] = i;
        return storages[i];
    }

    /// <summary>Strip .balance suffix from mount path.</summary>
    public static string GetActualMountPath(string mountPath)
    {
        var idx = mountPath.LastIndexOf(".balance", StringComparison.Ordinal);
        return idx >= 0 ? mountPath[..idx] : mountPath;
    }

    private static bool IsSubPath(string mountPath, string path)
    {
        mountPath = NormalizeMountPath(mountPath);
        path = NormalizeMountPath(path);
        if (string.Equals(mountPath, path, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = mountPath.EndsWith('/') ? mountPath : mountPath + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Virtual Files

    /// <summary>
    /// Get virtual directory entries under the given prefix path.
    /// Mirrors Go's getStorageVirtualFilesByPath.
    /// </summary>
    public List<ObjItem> GetVirtualFilesByPath(string prefix)
        => GetVirtualFilesByPath(prefix, false);

    /// <summary>
    /// Get virtual directory entries with optional storage details.
    /// </summary>
    public List<ObjItem> GetVirtualFilesByPath(string prefix, bool withStorageDetails, CancellationToken ct = default)
    {
        var files = new List<ObjItem>();
        var seen = new Dictionary<string, int>();

        if (!prefix.EndsWith('/')) prefix += "/";

        var storages = _drivers.Values
            .OfType<IFileSystemDriverMeta>()
            .Where(d => d.Storage != null && !d.Storage.Disabled)
            .OrderBy(d => d.Storage.Order)
            .ThenBy(d => d.Storage.MountPath)
            .ToList();

        foreach (var driverEntry in storages)
        {
            var storage = driverEntry.Storage;
            var mountPath = GetActualMountPath(storage.MountPath);

            if (!mountPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = mountPath[prefix.Length..];
            if (string.IsNullOrEmpty(relative)) continue;

            var name = relative.Split('/')[0];
            var hasMore = relative.Length > name.Length;

            if (seen.TryGetValue(name, out var idx))
            {
                if (!hasMore)
                {
                    files[idx].Mask = ObjMask.Locked | ObjMask.Virtual;
                    if (withStorageDetails)
                        files[idx].MountDetails = GetDetailsForDriver(driverEntry);
                }
                continue;
            }

            seen[name] = files.Count;
            var obj = new ObjItem
            {
                Name = name,
                IsFolder = true,
                Modified = storage.Modified,
                Mask = hasMore ? (ObjMask.ReadOnly | ObjMask.Virtual) : (ObjMask.Locked | ObjMask.Virtual),
                MountDetails = withStorageDetails ? GetDetailsForDriver(driverEntry) : null
            };
            files.Add(obj);
        }

        return files;
    }

    private StorageDetails? GetDetailsForDriver(IFileSystemDriverMeta driver)
    {
        if (driver is not IFileSystemWithDetails withDetails) return null;
        try
        {
            var task = withDetails.GetDetailsAsync();
            if (task.Wait(TimeSpan.FromSeconds(1)))
                return task.Result;
        }
        catch { }
        return null;
    }

    #endregion

    #region Mount Management

    public IFileSystem? GetFileSystem(string mountPath)
    {
        mountPath = NormalizeMountPath(mountPath);
        return _drivers.GetValueOrDefault(mountPath);
    }

    public Storage? GetStorage(string mountPath)
    {
        mountPath = NormalizeMountPath(mountPath);
        return _storages.GetValueOrDefault(mountPath);
    }

    public List<Storage> GetAllStorages()
        => _storages.Values.Where(s => s != null).ToList()!;

    public Dictionary<string, IFileSystem> GetAllMounts()
    {
        return new Dictionary<string, IFileSystem>(_drivers);
    }

    public List<string> GetVirtualRootEntries()
    {
        return _drivers.Keys.Select(k => k.TrimStart('/')).Where(k => !string.IsNullOrEmpty(k)).ToList();
    }

    public bool HasMount(string mountPath)
    {
        mountPath = NormalizeMountPath(mountPath);
        return _drivers.ContainsKey(mountPath);
    }

    public bool HasStorage(string mountPath)
    {
        mountPath = NormalizeMountPath(mountPath);
        return _storages.ContainsKey(mountPath);
    }

    public MountFileSystem GetMountFileSystem() => _mountFs;

    private void UnmountInternal(string mountPath)
    {
        if (_drivers.ContainsKey(mountPath))
        {
            try { _mountFs.Unmount(new UPath(mountPath)); } catch { }
        }
    }

    #endregion

    #region Helpers

    private static string NormalizeMountPath(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join("/", segments);
    }

    /// <summary>
    /// Handles Go's JSON encoding where bool fields may be stored as strings.
    /// </summary>
    private sealed class FlexibleBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.String => reader.GetString() is "true" or "1" or "yes",
                JsonTokenType.Number => reader.TryGetInt32(out var n) && n != 0,
                _ => throw new JsonException($"Cannot convert {reader.TokenType} to Boolean")
            };
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }
    }

    #endregion

    public void Dispose()
    {
        foreach (var driver in _drivers.Values)
        {
            if (driver is IFileSystemDriverMeta meta)
            {
                try { meta.DropAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            }
            try { driver.Dispose(); } catch { }
        }
        _drivers.Clear();
        _storages.Clear();
        _mountFs.Dispose();
    }
}
