using System.Reflection;
using OpenList.Core.Drivers;
using OpenList.Core.Models;
using Zio;
using Zio.FileSystems;

namespace OpenList.Core.Drivers.Base;

/// <summary>
/// Base class for all Zio-based storage drivers.
/// Inherits from Zio.FileSystem to provide IFileSystem implementation,
/// and implements IFileSystemDriverMeta for driver lifecycle management.
/// </summary>
public abstract class ZioDriverBase : FileSystem, IFileSystemDriverMeta
{
    protected Storage _storage = null!;

    public abstract DriverConfig Config { get; }
    public Storage Storage
    {
        get => _storage;
        set => _storage = value;
    }
    public abstract object Addition { get; }

    public abstract DriverInfo GetDriverInfo();
    public abstract Task InitAsync(CancellationToken ct = default);
    public abstract Task DropAsync(CancellationToken ct = default);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { DropAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        }
        base.Dispose(disposing);
    }

    #region Default IFileSystem Impl overrides (throw NotSupportedException for unsupported operations)

    protected override void CreateDirectoryImpl(UPath path) => throw new NotSupportedException();
    protected override bool DirectoryExistsImpl(UPath path) => false;
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath) => throw new NotSupportedException();
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive) => throw new NotSupportedException();
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite) => throw new NotSupportedException();
    protected override void ReplaceFileImpl(UPath srcPath, UPath destPath, UPath destBackupPath, bool ignoreMetadataErrors) => throw new NotSupportedException();
    protected override long GetFileLengthImpl(UPath path) => throw new NotSupportedException();
    protected override bool FileExistsImpl(UPath path) => false;
    protected override void MoveFileImpl(UPath srcPath, UPath destPath) => throw new NotSupportedException();
    protected override void DeleteFileImpl(UPath path) => throw new NotSupportedException();
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share) => throw new NotSupportedException();
    protected override FileAttributes GetAttributesImpl(UPath path) => throw new NotSupportedException();
    protected override void SetAttributesImpl(UPath path, FileAttributes attributes) => throw new NotSupportedException();
    protected override DateTime GetCreationTimeImpl(UPath path) => DateTime.MinValue;
    protected override void SetCreationTimeImpl(UPath path, DateTime time) { }
    protected override DateTime GetLastAccessTimeImpl(UPath path) => DateTime.MinValue;
    protected override void SetLastAccessTimeImpl(UPath path, DateTime time) { }
    protected override DateTime GetLastWriteTimeImpl(UPath path) => DateTime.MinValue;
    protected override void SetLastWriteTimeImpl(UPath path, DateTime time) { }
    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget) => [];
    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate) => [];
    protected override IFileSystemWatcher WatchImpl(UPath path) => throw new NotSupportedException();
    protected override bool CanWatchImpl(UPath path) => false;
    protected override string ConvertPathToInternalImpl(UPath path) => path.FullName;
    protected override UPath ConvertPathFromInternalImpl(string systemPath) => new UPath(systemPath);
    protected override (IFileSystem FileSystem, UPath Path) ResolvePathImpl(UPath path) => (this, path);
    protected override void CreateSymbolicLinkImpl(UPath path, UPath pathToTarget) => throw new NotSupportedException();
    protected override bool TryResolveLinkTargetImpl(UPath linkPath, out UPath resolvedPath) { resolvedPath = UPath.Empty; return false; }

    #endregion

    #region Helper methods for building DriverInfo

    protected static List<DriverItem> BuildItemsFromObject(object obj, HashSet<string>? excludeNames = null)
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
                Type = typeAttr?.Type ?? MapPropertyType(prop.PropertyType),
                Default = FormatDefaultValue(prop.GetValue(obj)),
                Options = optionsAttr?.Options,
                Required = prop.GetCustomAttributes<System.ComponentModel.DataAnnotations.RequiredAttribute>().Any(),
                Help = descAttr?.Description
            };
            items.Add(item);
        }

        return items;
    }

    protected static string ToSnakeCase(string name)
        => string.Concat(name.Select((c, i) =>
            i > 0 && (char.IsUpper(c) || char.IsDigit(c)) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()))
            .Replace("__", "_");

    private static string MapPropertyType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null) type = underlying;

        if (type == typeof(bool)) return "bool";
        if (type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double))
            return "number";
        if (type == typeof(string)) return "string";
        return type.Name.ToLowerInvariant();
    }

    private static string? FormatDefaultValue(object? value)
    {
        if (value == null) return null;
        if (value is bool b) return b ? "true" : "false";
        return value.ToString();
    }

    #endregion
}

