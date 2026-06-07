using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

public static class S3Endpoints
{
    private static readonly ConcurrentDictionary<string, MultipartUploadState> _multipartUploads = new();
    public static IEndpointRouteBuilder MapS3Api(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/s3");

        // ListBuckets
        group.MapGet("", async (HttpContext ctx, IStorageService storage) =>
        {
            if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
            var (storages, _) = await storage.ListStoragesAsync(1, 100);
            var xml = BuildListBucketsXml(storages);
            return Results.Text(xml, "application/xml");
        });

        // HeadBucket
        group.MapMethods("{bucket}", ["HEAD"], async (HttpContext ctx, IStorageService storage, string bucket) =>
        {
            if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
            var (storages, _) = await storage.ListStoragesAsync(1, 100);
            return storages.Any(s => s.MountPath.Trim('/') == bucket) ? Results.Ok() : Results.NotFound();
        });

        // ListObjects
        group.MapGet("{bucket}", async (HttpContext ctx, IStorageService storage, string bucket) =>
        {
            // GetBucketVersioning
            if (ctx.Request.Query.ContainsKey("versioning"))
            {
                if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
                var versioningXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><VersioningConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"/>";
                return Results.Text(versioningXml, "application/xml");
            }

            // ListObjectsV2 (default)
            if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
            var fs = ctx.RequestServices.GetRequiredService<IFileSystemService>();
            var prefix = ctx.Request.Query["prefix"].ToString();
            var maxKeysStr = ctx.Request.Query["max-keys"].ToString();
            var maxKeys = int.TryParse(maxKeysStr, out var mk) ? mk : 1000;

            var path = string.IsNullOrEmpty(prefix) ? $"/{bucket}" : $"/{bucket}/{prefix.TrimEnd('/')}";
            var objs = await fs.ListAsync(path);
            var xml = BuildListObjectsXml(bucket, prefix, maxKeys, objs.Take(maxKeys));
            return Results.Text(xml, "application/xml");
        });

        // GET: GetObjectAcl (?acl)
        group.MapGet("{bucket}/{*key}", async (HttpContext ctx, IFileSystemService fs, string bucket, string key) =>
        {
            if (!ctx.Request.Query.ContainsKey("acl")) goto skipAcl;
            if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);

            var xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><AccessControlPolicy xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Owner><ID>openlist</ID><DisplayName>OpenList</DisplayName></Owner><AccessControlList><Grant><Grantee xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"CanonicalUser\"><ID>openlist</ID><DisplayName>OpenList</DisplayName></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>";
            return Results.Text(xml, "application/xml");

        skipAcl:
            // Regular GetObject
            if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
            var path = $"/{bucket}/{key}";
            var obj = await fs.GetAsync(path);
            if (obj == null) return Results.NotFound();

            var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
            if (link == null) return Results.NotFound();
            if (link.URL.StartsWith("http")) return Results.Redirect(link.URL);
            if (System.IO.File.Exists(link.URL))
                return Results.File(link.URL, GetContentType(key), enableRangeProcessing: true);
            return Results.NotFound();
        });

        // HeadObject
        group.MapMethods("{bucket}/{*key}", ["HEAD"], async (HttpContext ctx, IFileSystemService fs, string bucket, string key) =>
        {
            if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
            var path = $"/{bucket}/{key}";
            var obj = await fs.GetAsync(path);
            if (obj == null) return Results.NotFound();

            ctx.Response.Headers.ContentLength = obj.GetSize();
            ctx.Response.Headers.ContentType = GetContentType(key);
            ctx.Response.Headers.LastModified = obj.GetModTime().ToString("R");
            ctx.Response.Headers.ETag = $"\"{obj.GetHash()}\"";
            return Results.Ok();
        });

        // === Multipart Upload + PutObject ===

        // PUT: UploadPart (?partNumber&uploadId) or regular PutObject
        group.MapPut("{bucket}/{*key}", async (HttpContext ctx, IFileSystemService fs, string bucket, string key) =>
        {
            // PutObjectAcl - accept for compatibility
            if (ctx.Request.Query.ContainsKey("acl"))
            {
                if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
                return Results.Ok();
            }

            var uploadId = ctx.Request.Query["uploadId"].ToString();
            var partNumberStr = ctx.Request.Query["partNumber"].ToString();

            // UploadPart
            if (!string.IsNullOrEmpty(uploadId) && int.TryParse(partNumberStr, out var partNumber))
            {
                if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
                if (!_multipartUploads.TryGetValue(uploadId, out var uploadState))
                    return Results.StatusCode(404);

                var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openlist-multipart");
                System.IO.Directory.CreateDirectory(tempDir);
                var tempFile = System.IO.Path.Combine(tempDir, $"{uploadId}_{partNumber}");
                var size = ctx.Request.ContentLength ?? 0;

                using (var fs2 = System.IO.File.Create(tempFile))
                    await ctx.Request.Body.CopyToAsync(fs2);

                var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(await System.IO.File.ReadAllBytesAsync(tempFile))).ToLowerInvariant();
                uploadState.Parts[partNumber] = new MultipartPartInfo
                {
                    PartNumber = partNumber,
                    ETag = $"\"{md5}\"",
                    Size = size,
                    TempFile = tempFile
                };

                ctx.Response.Headers.ETag = $"\"{md5}\"";
                return Results.Ok();
            }

            // Regular PutObject
            if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
            var path = $"/{bucket}/{key}";
            var fileName = System.IO.Path.GetFileName(key);
            var parentDir = $"/{bucket}";
            var parentKey = System.IO.Path.GetDirectoryName(key);
            if (!string.IsNullOrEmpty(parentKey))
                parentDir = $"/{bucket}/{parentKey.Replace('\\', '/')}";

            var parentObj = await fs.GetAsync(parentDir);
            if (parentObj == null || !parentObj.IsDir())
                return Results.StatusCode(404);

            var stream = ctx.Request.Body;
            var size2 = ctx.Request.ContentLength ?? 0;

            var fileStreamer = new FileStreamer
            {
                Obj = new ObjItem { Name = fileName, IsFolder = false, Size = size2 },
                Stream = stream,
                Size = size2,
                Mimetype = ctx.Request.ContentType
            };

            await fs.PutAsync(parentDir, fileStreamer, _ => { });

            var etag = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                size2 > 0 ? await ReadAllBytesAsync(stream) : [])).ToLowerInvariant();
            ctx.Response.Headers.ETag = $"\"{etag}\"";
            return Results.Ok();
        });

        // POST: InitiateMultipartUpload (?uploads) or CompleteMultipartUpload (?uploadId)
        group.MapPost("{bucket}/{*key}", async (HttpContext ctx, IFileSystemService fs, string bucket, string key) =>
        {
            // InitiateMultipartUpload
            if (ctx.Request.Query.ContainsKey("uploads"))
            {
                if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
                var uploadId = Guid.NewGuid().ToString("N");
                _multipartUploads[uploadId] = new MultipartUploadState
                {
                    Bucket = bucket,
                    Key = key,
                    Parts = []
                };
                var xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>{bucket}</Bucket><Key>{key}</Key><UploadId>{uploadId}</UploadId></InitiateMultipartUploadResult>";
                return Results.Text(xml, "application/xml");
            }

            // CompleteMultipartUpload
            var uploadId2 = ctx.Request.Query["uploadId"].ToString();
            if (!string.IsNullOrEmpty(uploadId2))
            {
                if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
                if (!_multipartUploads.TryGetValue(uploadId2, out var uploadState))
                    return Results.StatusCode(404);

                var fileName = System.IO.Path.GetFileName(key);
                var parentDir = $"/{bucket}";
                var parentKey = System.IO.Path.GetDirectoryName(key);
                if (!string.IsNullOrEmpty(parentKey))
                    parentDir = $"/{bucket}/{parentKey.Replace('\\', '/')}";

                var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openlist-multipart");
                var finalFile = System.IO.Path.Combine(tempDir, $"final_{uploadId2}");
                var totalSize = 0L;

                await using (var finalStream = System.IO.File.Create(finalFile))
                {
                    foreach (var part in uploadState.Parts.OrderBy(p => p.Key))
                    {
                        if (part.Value.TempFile != null && System.IO.File.Exists(part.Value.TempFile))
                        {
                            using var partStream = System.IO.File.OpenRead(part.Value.TempFile);
                            await partStream.CopyToAsync(finalStream);
                            totalSize += part.Value.Size;
                        }
                    }
                }

                await using (var finalReadStream = System.IO.File.OpenRead(finalFile))
                {
                    var fileStreamer = new FileStreamer
                    {
                        Obj = new ObjItem { Name = fileName, IsFolder = false, Size = totalSize },
                        Stream = finalReadStream,
                        Size = totalSize
                    };
                    await fs.PutAsync(parentDir, fileStreamer, _ => { });
                }

                foreach (var part in uploadState.Parts.Values)
                {
                    if (part.TempFile != null)
                        try { System.IO.File.Delete(part.TempFile); } catch { }
                }
                try { System.IO.File.Delete(finalFile); } catch { }
                _multipartUploads.TryRemove(uploadId2, out _);

                var etag = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    System.IO.File.Exists(finalFile) ? await System.IO.File.ReadAllBytesAsync(finalFile) : [])).ToLowerInvariant();
                var xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><CompleteMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Location>http://localhost/{bucket}/{key}</Location><Bucket>{bucket}</Bucket><Key>{key}</Key><ETag>\"{etag}\"</ETag></CompleteMultipartUploadResult>";
                return Results.Text(xml, "application/xml");
            }

            return Results.NotFound();
        });

        // DELETE: AbortMultipartUpload (?uploadId) or regular DeleteObject
        group.MapDelete("{bucket}/{*key}", async (HttpContext ctx, IFileSystemService fs, string bucket, string key) =>
        {
            var uploadId = ctx.Request.Query["uploadId"].ToString();
            if (!string.IsNullOrEmpty(uploadId))
            {
                if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
                if (_multipartUploads.TryRemove(uploadId, out var uploadState))
                {
                    foreach (var part in uploadState.Parts.Values)
                    {
                        if (part.TempFile != null)
                            try { System.IO.File.Delete(part.TempFile); } catch { }
                    }
                }
                return Results.NoContent();
            }

            // Regular DeleteObject
            if (!await VerifyS3AuthAsync(ctx)) return Results.StatusCode(403);
            await fs.RemoveAsync($"/{bucket}/{key}");
            return Results.NoContent();
        });

        // PUT: PutObjectAcl (?acl)
        // Already handled in the MapPut handler above — if ?acl is present, accept and return OK
        // (S3 ACL is not actually enforced, we just accept the request for compatibility)

        return endpoints;
    }

    /// <summary>
    /// Verify S3 authentication: AWS Signature V4 or V2, or Bearer token.
    /// Falls back to allowing requests without auth (public access).
    /// </summary>
    private static Task<bool> VerifyS3AuthAsync(HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader)) return Task.FromResult(true); // Public access allowed

        // Bearer token - delegate to ASP.NET auth
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(true);

        // AWS Signature V4: AWS4-HMAC-SHA256 Credential=...
        if (authHeader.StartsWith("AWS4-HMAC-SHA256", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(VerifySignatureV4(ctx, authHeader));

        // AWS Signature V2: AWS AccessKeyId:Signature
        if (authHeader.StartsWith("AWS ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(VerifySignatureV2(ctx, authHeader));

        return Task.FromResult(false);
    }

    private static bool VerifySignatureV4(HttpContext ctx, string authHeader)
    {
        var parts = authHeader["AWS4-HMAC-SHA256 ".Length..].Split(',', StringSplitOptions.TrimEntries);
        string? signature = null;
        string? signedHeaders = null;
        string? credential = null;

        foreach (var part in parts)
        {
            if (part.StartsWith("Credential=")) credential = part["Credential=".Length..];
            else if (part.StartsWith("SignedHeaders=")) signedHeaders = part["SignedHeaders=".Length..];
            else if (part.StartsWith("Signature=")) signature = part["Signature=".Length..];
        }

        if (credential == null || signature == null || signedHeaders == null) return false;

        var credParts = credential.Split('/');
        if (credParts.Length < 5) return false;
        var accessKey = credParts[0];
        var date = credParts[1];
        var region = credParts[2];
        var service = credParts[3];

        var secretKey = GetSecretKey(ctx, accessKey);
        if (secretKey == null) return false;

        // Compute signing key
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        var kSigning = HmacSha256(kService, "aws4_request");

        // Build canonical request
        var method = ctx.Request.Method;
        var canonicalUri = ctx.Request.Path.Value ?? "/";
        var canonicalQuerystring = BuildCanonicalQueryString(ctx);
        var canonicalHeaders = BuildCanonicalHeaders(ctx, signedHeaders);
        var payloadHash = "UNSIGNED-PAYLOAD";

        var canonicalRequest = $"{method}\n{canonicalUri}\n{canonicalQuerystring}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        // Build string to sign
        var amzDate = ctx.Request.Headers["X-Amz-Date"].ToString();
        if (string.IsNullOrEmpty(amzDate)) amzDate = date + "T000000Z";
        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(canonicalRequest)}";

        var computedSig = HmacSha256Hex(kSigning, stringToSign);
        return string.Equals(signature, computedSig, StringComparison.OrdinalIgnoreCase);
    }

    private static bool VerifySignatureV2(HttpContext ctx, string authHeader)
    {
        var cred = authHeader["AWS ".Length..].Split(':');
        if (cred.Length != 2) return false;
        var accessKey = cred[0];
        var signature = cred[1];

        var secretKey = GetSecretKey(ctx, accessKey);
        if (secretKey == null) return false;

        var method = ctx.Request.Method;
        var contentMd5 = ctx.Request.Headers["Content-MD5"].ToString();
        var contentType = ctx.Request.ContentType ?? "";
        var date = ctx.Request.Headers["Date"].ToString();
        if (string.IsNullOrEmpty(date)) date = ctx.Request.Headers["X-Amz-Date"].ToString();

        var canonicalizedAmzHeaders = BuildCanonicalizedAmzHeaders(ctx);
        var canonicalizedResource = ctx.Request.Path.Value ?? "/";

        var stringToSign = $"{method}\n{contentMd5}\n{contentType}\n{date}\n{canonicalizedAmzHeaders}{canonicalizedResource}";

        var computedSig = Convert.ToBase64String(HmacSha1Raw(Encoding.UTF8.GetBytes(secretKey), stringToSign));
        return string.Equals(signature, computedSig, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSecretKey(HttpContext ctx, string accessKey)
    {
        var config = ctx.RequestServices.GetService<IConfiguration>();
        return config?[$"S3AccessKeys:{accessKey}"];
    }

    private static string BuildCanonicalQueryString(HttpContext ctx)
    {
        var qs = ctx.Request.Query;
        if (qs.Count == 0) return "";
        var items = qs.SelectMany(kvp => kvp.Value.Select(v => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(v)}"))
                      .OrderBy(x => x);
        return string.Join("&", items);
    }

    private static string BuildCanonicalHeaders(HttpContext ctx, string signedHeaders)
    {
        var headers = signedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var h in headers.OrderBy(x => x))
        {
            var value = ctx.Request.Headers[h].ToString();
            sb.AppendLine($"{h}:{value.Trim()}");
        }
        return sb.ToString();
    }

    private static string BuildCanonicalizedAmzHeaders(HttpContext ctx)
    {
        var amzHeaders = ctx.Request.Headers
            .Where(h => h.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        foreach (var h in amzHeaders)
        {
            sb.AppendLine($"{h.Key.ToLowerInvariant()}:{string.Join(",", h.Value).Trim()}");
        }
        return sb.ToString();
    }

    private static byte[] HmacSha256(byte[] key, string data)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string HmacSha256Hex(byte[] key, string data)
        => Convert.ToHexString(HmacSha256(key, data)).ToLowerInvariant();

    private static byte[] HmacSha1Raw(byte[] key, string data)
        => HMACSHA1.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Sha256Hex(string data)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static string BuildListBucketsXml(IEnumerable<Storage> storages)
    {
        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
        var root = doc.CreateElement("ListAllMyBucketsResult");
        root.SetAttribute("xmlns", "http://s3.amazonaws.com/doc/2006-03-01/");
        doc.AppendChild(root);

        var owner = doc.CreateElement("Owner");
        var ownerId = doc.CreateElement("ID"); ownerId.InnerText = "openlist";
        var ownerName = doc.CreateElement("DisplayName"); ownerName.InnerText = "OpenList";
        owner.AppendChild(ownerId); owner.AppendChild(ownerName);
        root.AppendChild(owner);

        var buckets = doc.CreateElement("Buckets");
        foreach (var s in storages)
        {
            var bucket = doc.CreateElement("Bucket");
            var name = doc.CreateElement("Name"); name.InnerText = s.MountPath.Trim('/');
            var created = doc.CreateElement("CreationDate"); created.InnerText = s.Modified.ToString("o");
            bucket.AppendChild(name); bucket.AppendChild(created);
            buckets.AppendChild(bucket);
        }
        root.AppendChild(buckets);
        return doc.OuterXml;
    }

    private static string BuildListObjectsXml(string bucket, string? prefix, int maxKeys, IEnumerable<IObj> objs)
    {
        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
        var root = doc.CreateElement("ListBucketResult");
        root.SetAttribute("xmlns", "http://s3.amazonaws.com/doc/2006-03-01/");
        doc.AppendChild(root);

        var nameEl = doc.CreateElement("Name"); nameEl.InnerText = bucket; root.AppendChild(nameEl);
        var prefixEl = doc.CreateElement("Prefix"); prefixEl.InnerText = prefix ?? ""; root.AppendChild(prefixEl);
        var maxKeysEl = doc.CreateElement("MaxKeys"); maxKeysEl.InnerText = maxKeys.ToString(); root.AppendChild(maxKeysEl);
        var isTruncated = doc.CreateElement("IsTruncated"); isTruncated.InnerText = "false"; root.AppendChild(isTruncated);

        foreach (var obj in objs)
        {
            if (obj.IsDir())
            {
                var cp = doc.CreateElement("CommonPrefixes");
                var p = doc.CreateElement("Prefix"); p.InnerText = obj.GetName() + "/";
                cp.AppendChild(p); root.AppendChild(cp);
            }
            else
            {
                var contents = doc.CreateElement("Contents");
                var key = doc.CreateElement("Key"); key.InnerText = obj.GetName();
                var lastMod = doc.CreateElement("LastModified"); lastMod.InnerText = obj.GetModTime().ToString("o");
                var size = doc.CreateElement("Size"); size.InnerText = obj.GetSize().ToString();
                var etag = doc.CreateElement("ETag"); etag.InnerText = $"\"{obj.GetHash()}\"";
                contents.AppendChild(key); contents.AppendChild(lastMod); contents.AppendChild(etag); contents.AppendChild(size);
                root.AppendChild(contents);
            }
        }
        return doc.OuterXml;
    }

    private static string GetContentType(string key)
    {
        var ext = System.IO.Path.GetExtension(key).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4", ".mp3" => "audio/mpeg", ".pdf" => "application/pdf",
            ".txt" => "text/plain", ".html" => "text/html", ".json" => "application/json",
            _ => "application/octet-stream"
        };
    }
}

public class MultipartUploadState
{
    public string Bucket { get; set; } = "";
    public string Key { get; set; } = "";
    public Dictionary<int, MultipartPartInfo> Parts { get; set; } = [];
}

public class MultipartPartInfo
{
    public int PartNumber { get; set; }
    public string ETag { get; set; } = "";
    public long Size { get; set; }
    public string? TempFile { get; set; }
}
