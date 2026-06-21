using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services.OfflineDownload;

/// <summary>
/// Simple HTTP/HTTPS download tool — no external daemon required.
/// Corresponds to Go: internal/offline_download/http/client.go
/// </summary>
public partial class SimpleHttpTool : IOfflineDownloadTool
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, DownloadState> _downloads = new();

    public string Name => "SimpleHttp";
    public bool IsReady => true;

    public SimpleHttpTool()
    {
        _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public Task<string?> InitAsync(CancellationToken ct = default)
        => Task.FromResult<string?>("ok");

    public Task<string> AddUrlAsync(AddUrlArgs args, CancellationToken ct = default)
    {
        var gid = Guid.NewGuid().ToString("N");
        var state = new DownloadState { Url = args.Url, TempDir = args.TempDir, DeletePolicy = args.DeletePolicy };
        _downloads[gid] = state;

        // Start download in background
        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadAsync(gid, state, ct);
            }
            catch (OperationCanceledException)
            {
                state.Error = "cancelled";
                state.Completed = true;
            }
            catch (Exception ex)
            {
                state.Error = ex.Message;
                state.Completed = true;
            }
        }, CancellationToken.None);

        return Task.FromResult(gid);
    }

    public Task RemoveAsync(string gid, CancellationToken ct = default)
    {
        if (_downloads.TryRemove(gid, out var state))
        {
            state.Cts.Cancel();
            // Clean up temp files
            try { if (Directory.Exists(state.TempDir)) Directory.Delete(state.TempDir, true); } catch { }
        }
        return Task.CompletedTask;
    }

    public Task<DownloadStatus> GetStatusAsync(string gid, CancellationToken ct = default)
    {
        if (!_downloads.TryGetValue(gid, out var state))
            return Task.FromResult(new DownloadStatus { Error = "download not found", Completed = true });

        var status = new DownloadStatus
        {
            TotalBytes = state.TotalBytes,
            Progress = state.TotalBytes > 0 ? (double)state.DownloadedBytes / state.TotalBytes * 100 : 0,
            Completed = state.Completed,
            Error = state.Error,
            StatusText = state.Completed
                ? (state.Error != null ? $"error: {state.Error}" : "completed")
                : $"downloading ({(state.TotalBytes > 0 ? (double)state.DownloadedBytes / state.TotalBytes * 100 : 0):F1}%)"
        };

        // Clean up completed downloads from tracking
        if (state.Completed)
            _downloads.TryRemove(gid, out _);

        return Task.FromResult(status);
    }

    private async Task DownloadAsync(string gid, DownloadState state, CancellationToken externalCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, state.Cts.Token);
        var ct = linkedCts.Token;

        var streamPut = state.DeletePolicy == "upload_download_stream";

        // Build request
        var method = streamPut ? HttpMethod.Head : HttpMethod.Get;
        using var request = new HttpRequestMessage(method, state.Url);

        if (streamPut)
            request.Headers.Range = new RangeHeaderValue(0, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Parse filename
        var filename = ParseFilenameFromContentDisposition(
            response.Content.Headers.ContentDisposition?.ToString());
        if (string.IsNullOrEmpty(filename))
            filename = SanitizeFilename(response.RequestMessage!.RequestUri!.AbsolutePath);
        if (string.IsNullOrEmpty(filename))
            filename = $"{response.RequestMessage.RequestUri.Host.Replace(':', '_')}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Random.Shared.Next():x}";

        var fileSize = response.Content.Headers.ContentLength ?? 0;

        if (streamPut)
        {
            // Stream mode: just report metadata, don't actually download
            if (fileSize == 0)
            {
                var contentRange = response.Content.Headers.ContentRange?.ToString();
                if (contentRange != null)
                    fileSize = ParseContentRangeTotal(contentRange);
            }
            state.TotalBytes = fileSize;
            state.TempDir = filename; // Go: task.TempDir = filename for stream mode
            state.Completed = true;
            return;
        }

        state.TotalBytes = fileSize;

        // Ensure temp directory exists
        Directory.CreateDirectory(state.TempDir);
        var filePath = Path.Combine(state.TempDir, filename);

        // Safety check: prevent path traversal
        var cleanTempDir = Path.GetFullPath(state.TempDir) + Path.DirectorySeparatorChar;
        var cleanFilePath = Path.GetFullPath(filePath);
        if (!cleanFilePath.StartsWith(cleanTempDir))
            throw new InvalidOperationException("filename contains illegal path traversal");

        // Download file with progress tracking
        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            state.DownloadedBytes = totalRead;
        }

        state.Completed = true;
    }

    private static string? ParseFilenameFromContentDisposition(string? contentDisposition)
    {
        if (string.IsNullOrEmpty(contentDisposition)) return null;

        // Try filename*=UTF-8'' first
        var utf8Match = Utf8FilenameRegex().Match(contentDisposition);
        if (utf8Match.Success)
        {
            try { return Uri.UnescapeDataString(utf8Match.Groups[1].Value); }
            catch { }
        }

        // Try filename="..."
        var quotedMatch = QuotedFilenameRegex().Match(contentDisposition);
        if (quotedMatch.Success) return quotedMatch.Groups[1].Value;

        // Try filename=...
        var plainMatch = PlainFilenameRegex().Match(contentDisposition);
        if (plainMatch.Success) return plainMatch.Groups[1].Value;

        return null;
    }

    private static string? SanitizeFilename(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return null;
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static long ParseContentRangeTotal(string contentRange)
    {
        // Format: "bytes start-end/total" or "bytes start-end/*"
        var match = ContentRangeRegex().Match(contentRange);
        if (match.Success && long.TryParse(match.Groups[3].Value, out var total))
            return total;
        if (match.Success && long.TryParse(match.Groups[1].Value, out var end))
            return end + 1;
        return 0;
    }

    [GeneratedRegex(@"filename\*\s*=\s*UTF-8''(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Utf8FilenameRegex();

    [GeneratedRegex(@"filename\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedFilenameRegex();

    [GeneratedRegex(@"filename\s*=\s*([^\s;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlainFilenameRegex();

    [GeneratedRegex(@"bytes\s+(\d+)-(\d+)/(\d+|\*)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentRangeRegex();

    private class DownloadState
    {
        public string Url { get; init; } = "";
        public string TempDir { get; set; } = "";
        public string DeletePolicy { get; init; } = "";
        public long TotalBytes;
        public long DownloadedBytes;
        public bool Completed;
        public string? Error;
        public CancellationTokenSource Cts { get; } = new();
    }
}
