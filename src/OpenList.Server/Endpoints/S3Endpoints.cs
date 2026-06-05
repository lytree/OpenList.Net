using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Server.Endpoints;

public static class S3Endpoints
{
    public static IEndpointRouteBuilder MapS3Api(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/s3");

        group.MapGet("", async (IStorageService storage) =>
        {
            var storages = await storage.ListStoragesAsync(1, 100);
            var xml = BuildListBucketsXml(storages);
            return Results.Text(xml, "application/xml");
        });

        group.MapMethods("{bucket}", ["HEAD"], async (IStorageService storage, string bucket) =>
        {
            var storages = await storage.ListStoragesAsync(1, 100);
            return storages.Any(s => s.MountPath.Trim('/') == bucket) ? Results.Ok() : Results.NotFound();
        });

        group.MapGet("{bucket}", async (IFileSystemService fs, IStorageService storage, string bucket, string? prefix, string? delimiter, int? maxKeys) =>
        {
            var path = $"/{bucket}";
            if (!string.IsNullOrEmpty(prefix)) path = $"/{bucket}/{prefix}";
            var objs = await fs.ListAsync(path);
            maxKeys ??= 1000;
            var xml = BuildListObjectsXml(bucket, prefix, maxKeys.Value, objs.Take(maxKeys.Value));
            return Results.Text(xml, "application/xml");
        });

        group.MapGet("{bucket}/{*key}", async (IFileSystemService fs, HttpContext ctx, string bucket, string key) =>
        {
            var path = $"/{bucket}/{key}";
            var link = await fs.LinkAsync(path, new LinkArgs { IP = ctx.Connection.RemoteIpAddress?.ToString() ?? "" });
            if (link == null) return Results.NotFound();
            if (link.URL.StartsWith("http")) return Results.Redirect(link.URL);
            if (System.IO.File.Exists(link.URL)) return Results.File(link.URL, GetContentType(key), enableRangeProcessing: true);
            return Results.NotFound();
        });

        group.MapMethods("{bucket}/{*key}", ["HEAD"], async (IFileSystemService fs, string bucket, string key) =>
        {
            var path = $"/{bucket}/{key}";
            var obj = await fs.GetAsync(path);
            if (obj == null) return Results.NotFound();
            return Results.Ok();
        });

        group.MapPut("{bucket}/{*key}", async (HttpContext ctx) =>
        {
            using var stream = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(stream);
            var etag = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream.ToArray())).ToLowerInvariant();
            ctx.Response.Headers.ETag = $"\"{etag}\"";
            return Results.Ok();
        }).RequireAuthorization();

        group.MapDelete("{bucket}/{*key}", async (IFileSystemService fs, string bucket, string key) =>
        {
            await fs.RemoveAsync($"/{bucket}/{key}");
            return Results.NoContent();
        }).RequireAuthorization();

        return endpoints;
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
