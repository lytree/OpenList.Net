using OpenList.Core.Drivers;
using OpenList.Core.Models;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OpenList.Infrastructure.Services;

public interface IDriverFactory
{
    /// <summary>
    /// Initialize and load a driver for the given storage.
    /// Go: initStorage + storagesMap.Store. Stores driver even on init failure (with error status).
    /// </summary>
    void LoadDriver(Storage storage);
    /// <summary>
    /// Drop and remove the driver at the given mount path.
    /// Go: storageDriver.Drop + storagesMap.Delete
    /// </summary>
    void UnloadDriver(string mountPath);
    IDriver? GetDriver(string mountPath);
    DriverInfo GetDriverInfo(string driverName);
    List<DriverInfo> ListDriverInfos();
    List<string> ListDriverNames();
    /// <summary>
    /// Check if a storage is loaded at the given mount path.
    /// Go: HasStorage
    /// </summary>
    bool HasStorage(string mountPath);
    /// <summary>
    /// Find the storage whose actual mount path (stripped of .balance suffix) is the longest prefix match for the given path.
    /// Go: getStoragesByPath → longest match
    /// </summary>
    Storage? FindStorageByPath(string path);
    /// <summary>
    /// Get balanced storage for a path (supports .balance suffix for load balancing).
    /// Go: GetBalancedStorage
    /// </summary>
    IDriver? GetBalancedStorage(string path);
    List<Storage> GetAllStorages();
    /// <summary>
    /// Get virtual directory entries under the given path (Go: getStorageVirtualFilesByPath).
    /// </summary>
    List<ObjItem> GetVirtualFilesByPath(string prefix);
    /// <summary>
    /// Get virtual directory entries with optional storage details (Go: GetStorageVirtualFilesWithDetailsByPath).
    /// </summary>
    List<ObjItem> GetVirtualFilesByPath(string prefix, bool withStorageDetails, CancellationToken ct = default);
    /// <summary>
    /// Reload a storage driver: drop existing, re-init with new storage config.
    /// Go: Drop + initStorage (in UpdateStorage)
    /// </summary>
    void ReloadDriver(Storage oldStorage, Storage newStorage);
    /// <summary>
    /// Strip .balance suffix from mount path.
    /// Go: utils.GetActualMountPath
    /// </summary>
    static string GetActualMountPath(string mountPath)
    {
        var idx = mountPath.LastIndexOf(".balance", StringComparison.Ordinal);
        return idx >= 0 ? mountPath[..idx] : mountPath;
    }
    /// <summary>
    /// Save the driver's addition config back to storage (Go: MustSaveDriverStorage).
    /// Returns the serialized addition JSON.
    /// </summary>
    string? SerializeAddition(IDriver driver);
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
    private readonly ConcurrentDictionary<string, int> _balanceMap = new();
    private readonly ILogger<DriverFactory>? _logger;

    // Go: storageHooks
    private readonly List<Action<string, IDriver>> _storageHooks = [];

    public const string StatusWork = "work";
    public const string StatusDisabled = "disabled";

    /// <summary>
    /// Strip .balance suffix from mount path.
    /// Go: utils.GetActualMountPath
    /// </summary>
    public static string GetActualMountPath(string mountPath)
    {
        var idx = mountPath.LastIndexOf(".balance", StringComparison.Ordinal);
        return idx >= 0 ? mountPath[..idx] : mountPath;
    }

    public DriverFactory(ILogger<DriverFactory>? logger = null)
    {
        _logger = logger;
        RegisterDrivers();
    }

    /// <summary>Go: RegisterStorageHook</summary>
    public void RegisterStorageHook(Action<string, IDriver> hook) => _storageHooks.Add(hook);

    private void CallStorageHooks(string type, IDriver driver)
    {
        foreach (var hook in _storageHooks)
        {
            try { hook(type, driver); } catch { }
        }
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

    /// <summary>
    /// Go: initStorage — unmarshal addition, init, store in map (even on failure), set status, save addition back.
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
    /// Go: UpdateStorage → Drop + initStorage. Handles mount path rename.
    /// </summary>
    public void ReloadDriver(Storage oldStorage, Storage newStorage)
    {
        // If mount path changed, remove old entry
        if (oldStorage.MountPath != newStorage.MountPath)
        {
            UnloadDriver(oldStorage.MountPath);
        }

        // Get existing driver or create new one
        IDriver? driver = null;
        if (oldStorage.MountPath == newStorage.MountPath)
            driver = GetDriver(oldStorage.MountPath);

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
            // Drop existing driver before re-init (Go: storageDriver.Drop)
            try { driver.DropAsync().GetAwaiter().GetResult(); } catch { }
        }

        InitAndStoreDriver(newStorage, driver);
        Task.Run(() => CallStorageHooks("update", driver));
    }

    /// <summary>
    /// JSON options for addition deserialization — handles Go's flexible JSON encoding
    /// where booleans may be stored as strings ("true"/"false") and numbers as strings.
    /// </summary>
    private static readonly JsonSerializerOptions AdditionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new FlexibleBoolConverter() }
    };

    private void InitAndStoreDriver(Storage storage, IDriver driver)
    {
        driver.SetStorage(storage);
        var driverStorage = driver.GetStorage();

        try
        {
            // Unmarshal addition JSON into driver's addition object (Go: utils.Json.UnmarshalFromString)
            var addition = driver.GetAddition();
            if (addition != null && !string.IsNullOrEmpty(driverStorage.Addition) && driverStorage.Addition != "{}")
            {
                try
                {
                    var additionType = addition.GetType();
                    var deserialized = JsonSerializer.Deserialize(driverStorage.Addition, additionType, AdditionJsonOptions);
                    if (deserialized != null)
                    {
                        // Copy properties from deserialized object to the driver's addition instance
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

            // Init driver (Go: storageDriver.Init)
            driver.InitAsync().GetAwaiter().GetResult();
            driverStorage.Status = StatusWork;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize driver '{Driver}' for storage '{MountPath}'", storage.Driver, storage.MountPath);
            driverStorage.Status = ex.Message;
        }

        // Go: storagesMap.Store — ALWAYS store, even on init failure
        _drivers[driverStorage.MountPath] = driver;

        // Go: MustSaveDriverStorage — save addition back to DB after init
        var additionJson = SerializeAddition(driver);
        if (additionJson != null)
        {
            driverStorage.Addition = additionJson;
        }
    }

    /// <summary>
    /// Handles Go's JSON encoding where bool fields may be stored as strings
    /// (e.g., "true", "false", "1", "0") or numbers (1, 0).
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

    public string? SerializeAddition(IDriver driver)
    {
        try
        {
            var addition = driver.GetAddition();
            if (addition == null) return null;
            return JsonSerializer.Serialize(addition);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to serialize addition for driver");
            return null;
        }
    }

    public void UnloadDriver(string mountPath)
    {
        if (_drivers.TryRemove(mountPath, out var driver))
        {
            try { driver.DropAsync().GetAwaiter().GetResult(); } catch { }
            Task.Run(() => CallStorageHooks("del", driver));
        }
    }

    public IDriver? GetDriver(string mountPath) => _drivers.GetValueOrDefault(FixAndCleanPath(mountPath));

    public bool HasStorage(string mountPath) => _drivers.ContainsKey(FixAndCleanPath(mountPath));

    /// <summary>Returns cached DriverInfo — mirrors Go's GetDriverInfoMap</summary>
    public DriverInfo GetDriverInfo(string driverName)
        => _driverInfoMap.GetValueOrDefault(driverName) ?? new DriverInfo();

    /// <summary>Returns all cached DriverInfos — mirrors Go's GetDriverInfoMap</summary>
    public List<DriverInfo> ListDriverInfos() => [.. _driverInfoMap.Values];

    public List<string> ListDriverNames() => [.. _driverInfoMap.Keys.OrderBy(k => k)];

    /// <summary>
    /// Go: getStoragesByPath — longest match with balance support.
    /// </summary>
    public Storage? FindStorageByPath(string path)
    {
        var storages = GetStoragesByPath(path);
        return storages.FirstOrDefault()?.GetStorage();
    }

    /// <summary>
    /// Go: GetBalancedStorage — round-robin across storages with same actual mount path.
    /// </summary>
    public IDriver? GetBalancedStorage(string path)
    {
        path = FixAndCleanPath(path);
        var storages = GetStoragesByPath(path);
        return storages.Count switch
        {
            0 => null,
            1 => storages[0],
            _ => RoundRobinBalance(storages)
        };
    }

    /// <summary>
    /// Go: getStoragesByPath — find all storages matching the path (longest prefix match), supports .balance suffix.
    /// </summary>
    private List<IDriver> GetStoragesByPath(string path)
    {
        var result = new List<IDriver>();
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

        // Go: sort by MountPath for deterministic order
        result.Sort((a, b) => string.Compare(a.GetStorage().MountPath, b.GetStorage().MountPath, StringComparison.Ordinal));
        return result;
    }

    private IDriver RoundRobinBalance(List<IDriver> storages)
    {
        var virtualPath = GetActualMountPath(storages[0].GetStorage().MountPath);
        var i = _balanceMap.GetOrAdd(virtualPath, 0);
        i = (i + 1) % storages.Count;
        _balanceMap[virtualPath] = i;
        return storages[i];
    }

    /// <summary>
    /// Go: utils.IsSubPath — path == subPath || subPath starts with path + "/"
    /// </summary>
    private static bool IsSubPath(string mountPath, string path)
    {
        mountPath = FixAndCleanPath(mountPath);
        path = FixAndCleanPath(path);
        if (string.Equals(mountPath, path, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = mountPath.EndsWith('/') ? mountPath : mountPath + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string FixAndCleanPath(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == ".." && stack.Count > 0) stack.Pop();
            else if (seg != "..") stack.Push(seg);
        }
        return "/" + string.Join("/", stack.Reverse());
    }

    public List<Storage> GetAllStorages()
        => _drivers.Values.Select(d => d.GetStorage()).Where(s => s != null).ToList()!;

    /// <summary>Go: getStorageVirtualFilesByPath — uses GetActualMountPath for balance support</summary>

    /// <summary>
    /// Get virtual directory entries under the given prefix path.
    /// Mirrors Go's getStorageVirtualFilesByPath: for each storage whose mount path
    /// starts with prefix+"/", extract the next path segment as a virtual directory name.
    /// </summary>
    public List<ObjItem> GetVirtualFilesByPath(string prefix)
        => GetVirtualFilesByPath(prefix, false);

    /// <summary>
    /// Get virtual directory entries with optional storage details.
    /// Mirrors Go's GetStorageVirtualFilesWithDetailsByPath: for each storage whose mount path
    /// starts with prefix+"/", extract the next path segment as a virtual directory name.
    /// When withStorageDetails is true, populates MountDetails from IDriverWithDetails.
    /// </summary>
    public List<ObjItem> GetVirtualFilesByPath(string prefix, bool withStorageDetails, CancellationToken ct = default)
    {
        var files = new List<ObjItem>();
        var seen = new Dictionary<string, int>();

        // Normalize prefix: ensure it ends with /
        if (!prefix.EndsWith('/')) prefix += "/";

        var storages = _drivers.Values
            .Select(d => d)
            .Where(d => d.GetStorage() != null && !d.GetStorage().Disabled)
            .OrderBy(d => d.GetStorage().Order)
            .ThenBy(d => d.GetStorage().MountPath)
            .ToList();

        foreach (var driverEntry in storages)
        {
            var storage = driverEntry.GetStorage();
            var mountPath = GetActualMountPath(storage.MountPath);

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
                    // Go: also update storage details for direct mount overriding intermediate
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

    /// <summary>
    /// Get storage details for a driver (Go: GetStorageDetails with 1s timeout).
    /// </summary>
    private StorageDetails? GetDetailsForDriver(IDriver driver)
    {
        if (driver is not IDriverWithDetails withDetails) return null;
        try
        {
            var task = withDetails.GetDetailsAsync();
            if (task.Wait(TimeSpan.FromSeconds(1)))
                return task.Result;
        }
        catch { /* ignore errors for storage details */ }
        return null;
    }
}
