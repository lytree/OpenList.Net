using System.IO;
using System.IO.Compression;
using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Archive-related helper methods for FS endpoints.
/// </summary>
internal static partial class FsHelpers
{
    internal static async Task<List<ArchiveEntryInfo>?> ReadArchiveEntriesAsync(IFileSystemService fs, string path, string? password)
    {
        var link = await fs.LinkAsync(path, new LinkArgs());
        if (link == null) return null;

        Stream? archiveStream = null;
        string? tempFile = null;

        try
        {
            // Try to open as local file first
            if (!string.IsNullOrEmpty(link.URL) && File.Exists(link.URL))
            {
                archiveStream = new FileStream(link.URL, FileMode.Open, FileAccess.Read);
            }
            else if (!string.IsNullOrEmpty(link.URL) && link.URL.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Download to temp file
                using var http = new HttpClient();
                var response = await http.GetAsync(link.URL, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                tempFile = Path.Combine(Path.GetTempPath(), $"openlist-archive-{Guid.NewGuid():N}");
                using (var fs2 = File.Create(tempFile))
                    await response.Content.CopyToAsync(fs2);
                archiveStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read);
            }
            else
            {
                return null;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".zip" => ReadZipEntries(archiveStream),
                _ => null // Unsupported archive format
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            archiveStream?.Dispose();
            if (tempFile != null && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    internal static List<ArchiveEntryInfo> ReadZipEntries(Stream stream)
    {
        var entries = new List<ArchiveEntryInfo>();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            entries.Add(new ArchiveEntryInfo
            {
                Name = entry.FullName,
                Size = entry.Length,
                IsDir = entry.FullName.EndsWith('/'),
                IsEncrypted = entry.CompressedLength == 0 && entry.Length > 0 // heuristic for encrypted entries
            });
        }
        return entries;
    }

    /// <summary>
    /// Extracts a single file from an archive (zip, tar.gz, rar, 7z, etc.) using SharpCompress.
    /// Returns null if the file is not found or extraction fails.
    /// </summary>
    internal static async Task<Stream?> ExtractArchiveFileAsync(string? archivePath, string innerPath, string? password)
    {
        if (string.IsNullOrEmpty(archivePath) || !System.IO.File.Exists(archivePath)) return null;

        try
        {
            await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            using var reader = SharpCompress.Readers.ReaderFactory.Open(fileStream);
            var normalizedName = innerPath.TrimStart('/');

            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                var entryKey = reader.Entry.Key ?? "";
                if (entryKey == normalizedName ||
                    entryKey.EndsWith("/" + normalizedName, StringComparison.Ordinal) ||
                    System.IO.Path.GetFileName(entryKey) == System.IO.Path.GetFileName(normalizedName))
                {
                    var ms = new MemoryStream();
                    using var entryStream = reader.OpenEntryStream();
                    await entryStream.CopyToAsync(ms);
                    ms.Position = 0;
                    return ms;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}

public class ArchiveEntryInfo
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public bool IsEncrypted { get; set; }
}
