using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.Local;

/// <summary>
/// Local filesystem driver. Mounts a directory on the server's filesystem.
/// Mirrors OpenList Go <c>drivers/local</c>.
/// </summary>
public sealed class LocalDriver : ZioDriverBase
{
    public override string DriverName => "Local";
    public override bool ReadOnly => false;
    public override bool IsCloudDriver => false;

    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new()
        {
            Name = "root_folder_path",
            Help = "Root folder path on the server filesystem",
            Required = true,
            ValueType = typeof(string),
        },
        new()
        {
            Name = "thumbnail",
            Help = "Enable thumbnail generation",
            ValueType = typeof(bool),
            Default = false,
        },
        new()
        {
            Name = "thumb_cache_folder",
            Help = "Cache folder for thumbnails",
            ValueType = typeof(string),
        },
    ];

    public LocalAddition Addition { get; private set; } = new();

    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<LocalAddition>();
        if (string.IsNullOrWhiteSpace(Addition.RootFolderPath))
            throw new InvalidOperationException("root_folder_path is required");

        var root = Path.GetFullPath(Addition.RootFolderPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"root folder not found: {root}");

        var physical = new PhysicalFileSystem();
        // PhysicalFileSystem uses its own OS-specific root; convert the absolute
        // filesystem path to a Zio absolute path under the physical fs.
        var zioRoot = physical.ConvertPathFromInternal(root);
        System = new SubFileSystem(physical, zioRoot);
        return Task.CompletedTask;
    }
}

public sealed class LocalAddition
{
    [JsonPropertyName("root_folder_path")]
    public string RootFolderPath { get; set; } = string.Empty;

    [JsonPropertyName("thumbnail")]
    public bool Thumbnail { get; set; }

    [JsonPropertyName("thumb_cache_folder")]
    public string? ThumbCacheFolder { get; set; }
}
