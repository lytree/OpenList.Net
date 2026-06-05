using System.Reflection;
using OpenList.Core.Drivers;
using OpenList.Core.Models;

namespace OpenList.Core.Drivers.Base;

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
        var addition = GetAddition();
        var additionalItems = BuildItemsFromObject(addition);

        // Build common items based on driver type
        var commonItems = new List<DriverItem>();
        if (this is RootPathDriver)
            commonItems.Add(new DriverItem { Name = "root_folder_path", Type = "string", Default = cfg.DefaultRoot, Required = true, Help = "Root folder path" });
        if (this is RootIdDriver)
            commonItems.Add(new DriverItem { Name = "root_folder_id", Type = "string", Default = cfg.DefaultRoot, Required = true, Help = "Root folder ID" });

        return new DriverInfo
        {
            Common = commonItems,
            Additional = additionalItems,
            Config = cfg
        };
    }

    private static List<DriverItem> BuildItemsFromObject(object obj)
    {
        var items = new List<DriverItem>();
        var type = obj.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in props)
        {
            var jsonName = ToSnakeCase(prop.Name);
            var item = new DriverItem
            {
                Name = jsonName,
                Type = prop.PropertyType.Name.ToLowerInvariant(),
                Default = prop.GetValue(obj)?.ToString(),
                Required = prop.GetCustomAttributes<System.ComponentModel.DataAnnotations.RequiredAttribute>().Any()
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
