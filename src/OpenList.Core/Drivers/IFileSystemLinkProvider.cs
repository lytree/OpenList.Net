using OpenList.Core.Models;
using Zio;

namespace OpenList.Core.Drivers;

/// <summary>
/// Extension interface for filesystems that can generate download links/URLs.
/// </summary>
public interface IFileSystemLinkProvider
{
    Task<Link?> GetLinkAsync(UPath path, LinkArgs args, CancellationToken ct = default);
}
