using System.Reflection;
using OpenList.Core.Drivers;
using OpenList.Core.Models;

namespace OpenList.Core.Drivers.Base;

/// <summary>
/// Specifies the options for a select-type driver item
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DriverItemOptionsAttribute : Attribute
{
    public string Options { get; }
    public DriverItemOptionsAttribute(string options) => Options = options;
}

/// <summary>
/// Specifies the type override for a driver item (e.g., "select", "text", "number")
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DriverItemTypeAttribute : Attribute
{
    public string Type { get; }
    public DriverItemTypeAttribute(string type) => Type = type;
}

/// <summary>
/// Base driver implementation with common functionality
/// </summary>
public abstract class DriverBase : IDriver
{
    protected Storage _storage = null!;
    private DriverConfig? _config;

    public abstract DriverConfig Config();
    public abstract object GetAddition();
    public abstract Task InitAsync(CancellationToken ct = default);
    public abstract Task DropAsync(CancellationToken ct = default);
    public abstract Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default);
    public abstract Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default);

    public virtual Storage GetStorage() => _storage;
    public virtual void SetStorage(Storage storage) => _storage = storage;

    public virtual DriverInfo Info()
    {
        var cfg = Config();

        // Use default addition instance for schema generation (no dependency on _storage)
        var additionType = GetAdditionType();
        var addition = additionType != null ? Activator.CreateInstance(additionType)! : new object();

        // Collect common item names to exclude from Additional
        var excludeNames = new HashSet<string>();

        // Build common items — matches Go getMainItems() order
        var commonItems = new List<DriverItem>
        {
            new() { Name = "mount_path", Type = "string", Required = true, Help = "The path you want to mount to, it is unique and cannot be repeated" },
            new() { Name = "order", Type = "number", Help = "use to sort" },
            new() { Name = "remark", Type = "text" }
        };

        if (!cfg.NoCache)
        {
            commonItems.Add(new DriverItem { Name = "cache_expiration", Type = "number", Default = "30", Required = true, Help = "The cache expiration time for this storage" });
            commonItems.Add(new DriverItem { Name = "custom_cache_policies", Type = "text", Help = "The cache expiration rules for this storage" });
        }

        // Root folder path/id (from driver Addition)
        if (this is RootPathDriver)
        {
            commonItems.Add(new DriverItem { Name = "root_folder_path", Type = "string", Default = cfg.DefaultRoot, Required = true });
            excludeNames.Add("root_folder_path");
        }
        if (this is RootIdDriver)
        {
            commonItems.Add(new DriverItem { Name = "root_folder_id", Type = "string", Default = cfg.DefaultRoot, Required = true });
            excludeNames.Add("root_folder_id");
        }

        // Proxy settings (from model.Storage.Proxy)
        if (cfg.MustProxy())
        {
            commonItems.Add(new DriverItem { Name = "webdav_policy", Type = "select", Options = "use_proxy_url,native_proxy", Default = "native_proxy", Required = true });
        }
        else
        {
            if (cfg.DefaultProxy())
            {
                commonItems.Add(new DriverItem { Name = "web_proxy", Type = "bool", Default = "true" });
                commonItems.Add(new DriverItem { Name = "webdav_policy", Type = "select", Options = "302_redirect,use_proxy_url,native_proxy", Default = "native_proxy", Required = true });
            }
            else
            {
                commonItems.Add(new DriverItem { Name = "web_proxy", Type = "bool" });
                commonItems.Add(new DriverItem { Name = "webdav_policy", Type = "select", Options = "302_redirect,use_proxy_url,native_proxy", Default = "302_redirect", Required = true });
            }

            if (cfg.ProxyRangeOption)
                commonItems.Add(new DriverItem { Name = "proxy_range", Type = "bool", Help = "Need to enable proxy" });
        }

        commonItems.Add(new DriverItem { Name = "down_proxy_url", Type = "text" });
        commonItems.Add(new DriverItem { Name = "disable_proxy_sign", Type = "bool", Default = "false", Help = "Disable sign for Download proxy URL" });

        // Sort settings (from model.Storage.Sort)
        if (cfg.LocalSort)
        {
            commonItems.Add(new DriverItem { Name = "order_by", Type = "select", Options = "name,size,modified" });
            commonItems.Add(new DriverItem { Name = "order_direction", Type = "select", Options = "asc,desc" });
        }

        commonItems.Add(new DriverItem { Name = "extract_folder", Type = "select", Options = "front,back" });
        commonItems.Add(new DriverItem { Name = "disable_index", Type = "bool", Default = "false", Required = true });
        commonItems.Add(new DriverItem { Name = "enable_sign", Type = "bool", Default = "false", Required = true });

        var additionalItems = BuildItemsFromObject(addition, excludeNames);

        return new DriverInfo
        {
            Common = commonItems,
            Additional = additionalItems,
            Config = cfg
        };
    }

    /// <summary>
    /// Returns the Type of the Addition class for this driver.
    /// Used by Info() to generate Additional items without depending on _storage.
    /// Override only if GetAddition() uses complex logic.
    /// </summary>
    protected virtual Type? GetAdditionType() => null;

    private static List<DriverItem> BuildItemsFromObject(object obj, HashSet<string>? excludeNames = null)
    {
        var items = new List<DriverItem>();
        var type = obj.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in props)
        {
            var jsonName = ToSnakeCase(prop.Name);
            if (excludeNames != null && excludeNames.Contains(jsonName))
                continue;

            var descAttr = prop.GetCustomAttributes<System.ComponentModel.DescriptionAttribute>().FirstOrDefault();
            var typeAttr = prop.GetCustomAttributes<DriverItemTypeAttribute>().FirstOrDefault();
            var optionsAttr = prop.GetCustomAttributes<DriverItemOptionsAttribute>().FirstOrDefault();

            var item = new DriverItem
            {
                Name = jsonName,
                Type = typeAttr?.Type ?? prop.PropertyType.Name.ToLowerInvariant(),
                Default = prop.GetValue(obj)?.ToString(),
                Options = optionsAttr?.Options,
                Required = prop.GetCustomAttributes<System.ComponentModel.DataAnnotations.RequiredAttribute>().Any(),
                Help = descAttr?.Description
            };
            items.Add(item);
        }

        return items;
    }

    private static string ToSnakeCase(string name)
        => string.Concat(name.Select((c, i) =>
            i > 0 && (char.IsUpper(c) || char.IsDigit(c)) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()))
            .Replace("__", "_");
}

/// <summary>
/// Base driver with root path support
/// </summary>
public abstract class RootPathDriver : DriverBase
{
    public string RootFolderPath { get; set; } = "";

    public string GetRootPath() => RootFolderPath;
    public void SetRootPath(string path) => RootFolderPath = path;
}

/// <summary>
/// Base driver with root ID support
/// </summary>
public abstract class RootIdDriver : DriverBase
{
    public string RootFolderID { get; set; } = "";

    public string GetRootId() => RootFolderID;
}
