using System.Reflection;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Auto-discovers <see cref="ZioDriverBase"/> subclasses from assemblies starting with "OpenList"
/// and mounts them into a Zio <c>MountFileSystem</c>. Adding a driver = create a class in
/// OpenList.Drivers — no manual registration needed.
/// Mirrors OpenList Go <c>internal/op</c> storage loading.
/// </summary>
public sealed class ZioMountService
{
    private readonly Dictionary<string, ZioDriverBase> _drivers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ZioDriverBase> Drivers => _drivers;

    public IEnumerable<Type> DiscoverDriverTypes()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("OpenList", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ZioDriverBase).IsAssignableFrom(t));
    }

    public Task LoadAllAsync(IEnumerable<Storage> storages, CancellationToken ct = default)
    {
        // TODO: instantiate drivers, call InitAsync, register in _drivers
        return Task.CompletedTask;
    }
}
