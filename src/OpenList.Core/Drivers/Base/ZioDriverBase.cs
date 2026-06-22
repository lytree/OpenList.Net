using System.Reflection;
using Zio;
using Zio.FileSystems;

namespace OpenList.Core.Drivers.Base;

/// <summary>
/// Base class for all OpenList storage drivers. Wraps a Zio <see cref="IFileSystem"/>
/// and exposes metadata used by <c>ZioMountService</c> for auto-discovery.
/// Implementations live in <c>OpenList.Drivers</c> and are discovered by assembly scan.
/// Uses composition over inheritance — drivers expose their underlying <see cref="FileSystem"/>
/// via <see cref="System"/> rather than subclassing it.
/// </summary>
public abstract class ZioDriverBase : IDisposable, IAsyncDisposable
{
    /// <summary>Underlying Zio filesystem (set by driver in <see cref="InitAsync"/>).</summary>
    public IFileSystem? System { get; protected set; }

    /// <summary>Driver type name shown in UI (e.g. "Local", "S3").</summary>
    public abstract string DriverName { get; }

    /// <summary>Whether the driver only supports read operations.</summary>
    public virtual bool ReadOnly => false;

    /// <summary>Whether the driver requires network access (cloud drivers).</summary>
    public virtual bool IsCloudDriver => false;

    /// <summary>Driver-specific configuration parsed from <c>Storage.Addition</c> JSON.</summary>
    public abstract Task InitAsync(DriverConfig config, CancellationToken ct = default);

    /// <summary>Release driver resources.</summary>
    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public virtual void Dispose()
    {
        (System as IDisposable)?.Dispose();
        System = null;
    }

    /// <summary>Attribute-driven config schema (auto-collected via reflection).</summary>
    public virtual IEnumerable<DriverItemAttributes> ConfigItems => [];
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class DriverConfigItemAttribute : Attribute
{
    public string Name { get; set; } = string.Empty;
    public string Help { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? Options { get; set; }
}
