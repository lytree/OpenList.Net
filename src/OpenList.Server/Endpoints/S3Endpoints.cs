using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using Zio;

namespace OpenList.Server.Endpoints;

/// <summary>
/// S3 兼容网关端点，路由位于 <c>/s3</c>。
/// 将 S3 桶（bucket）映射到挂载路径、对象键（key）映射到存储内的文件路径。
/// 对应 OpenList Go <c>server/s3</c>。
/// 鉴权：既接受 OpenList JWT（Authorization 头不带 "Bearer "），也接受 AWS SigV4
/// （Authorization: AWS4-HMAC-SHA256 ...），SigV4 的 access key 被当作 JWT 使用。
/// 需要先在中间件/此处完成令牌校验后的真实用户解析，否则一律 401。
/// </summary>
public static class S3Endpoints
{
    /// <summary>
    /// 注册 <c>/s3</c> 路由组。<c>/{bucket}[/{*key}]</c> 基线路由支持
    /// GET(列桶/列对象/取对象)、HEAD(桶/对象头)、PUT(上传)、DELETE(删除)。
    /// 无需 JWT 中间件（DisableAntiforgery），由 <see cref="ResolveUserAsync"/>
    /// 从 JWT 或 SigV4 头自行解析用户。
    /// </summary>
    public static IEndpointRouteBuilder MapS3Api(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/s3").WithTags("S3").DisableAntiforgery();

        // GET /s3 — 列出所有桶（对应挂载的存储）
        g.MapGet("/", ListBucketsAsync);

        // GET /s3/{bucket} — 列出桶内对象（支持 prefix/delimiter/max-keys 查询参数）
        g.MapGet("/{bucket}", ListObjectsAsync);

        // HEAD /s3/{bucket} — 探测桶是否存在
        g.MapMethods("/{bucket}", ["HEAD"], HeadBucketAsync);

        // GET /s3/{bucket}/{*key} — 获取对象（即下载文件）
        g.MapGet("/{bucket}/{*key}", GetObjectAsync);

        // HEAD /s3/{bucket}/{*key} — 获取对象元数据（仅响应头，不返回体）
        g.MapMethods("/{bucket}/{*key}", ["HEAD"], HeadObjectAsync);

        // PUT /s3/{bucket}/{*key} — 上传对象（请求体即文件字节）
        g.MapPut("/{bucket}/{*key}", PutObjectAsync);

        // DELETE /s3/{bucket}/{*key} — 删除对象
        g.MapDelete("/{bucket}/{*key}", DeleteObjectAsync);

        return app;
    }

    // --- 列出桶 ---

    /// <summary>GET /s3 — 列出所有已启用存储对应的桶并以 S3 ListAllMyBuckets 格式返回。</summary>
    private static async Task<IResult> ListBucketsAsync(
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        var storages = await storageSvc.ListAsync(ctx.RequestAborted);
        var buckets = storages.Data?.Content
            .Where(s => !s.Disabled)
            .Select(s => new S3Bucket
            {
                Name = s.MountPath.TrimStart('/'),
                CreationDate = s.CreatedAt.UtcDateTime,
            })
            .ToList() ?? [];

        var result = new S3ListAllMyBucketsResult
        {
            Owner = new S3Owner { Id = user.Username, DisplayName = user.Username },
            Buckets = buckets,
        };

        return XmlResult(result);
    }

    // --- Head bucket ---

    private static async Task<IResult> HeadBucketAsync(
        string bucket,
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        var mountPath = "/" + bucket;
        if (!mount.Mounted.ContainsKey(mountPath))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        ctx.Response.Headers["x-amz-bucket-region"] = "us-east-1";
        return Results.Ok();
    }

    // --- List objects ---

    private static async Task<IResult> ListObjectsAsync(
        string bucket,
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        var mountPath = "/" + bucket;
        if (!mount.Mounted.ContainsKey(mountPath))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        var prefix = ctx.Request.Query["prefix"].ToString();
        var delimiter = ctx.Request.Query["delimiter"].ToString();
        var maxKeys = int.TryParse(ctx.Request.Query["max-keys"], out var mk) ? mk : 1000;

        var rootPath = UPath.Root;
        var searchBase = string.IsNullOrEmpty(prefix) ? rootPath : (rootPath / prefix.TrimEnd('/'));

        var contents = new List<S3Object>();
        var commonPrefixes = new List<string>();

        try
        {
            if (!mount.Root.DirectoryExists(searchBase))
                return XmlResult(new S3ListBucketResult { Name = bucket, Contents = [], CommonPrefixes = [] });

            foreach (var entry in EnumerateRecursive(mount, searchBase, prefix))
            {
                if (contents.Count >= maxKeys) break;
                contents.Add(entry);
            }

            if (delimiter == "/")
            {
                // 按搜索基址下的一级前缀分组，把"目录"归入 CommonPrefixes
                var seen = new HashSet<string>();
                contents = [.. contents.Where(c =>
                {
                    var rel = c.Key.StartsWith(prefix) ? c.Key[prefix.Length..] : c.Key;
                    var slashIdx = rel.IndexOf('/');
                    if (slashIdx < 0) return true;
                    var cp = prefix + rel[..(slashIdx + 1)];
                    if (seen.Add(cp)) commonPrefixes.Add(cp);
                    return false;
                })];
            }
        }
        catch (Exception ex)
        {
            return S3Error("InternalError", ex.Message, 500);
        }

        var result = new S3ListBucketResult
        {
            Name = bucket,
            Prefix = prefix,
            Delimiter = string.IsNullOrEmpty(delimiter) ? null : delimiter,
            MaxKeys = maxKeys,
            IsTruncated = false,
            Contents = contents,
            CommonPrefixes = commonPrefixes.Select(p => new S3CommonPrefix { Prefix = p }).ToList(),
        };

        return XmlResult(result);
    }

    private static IEnumerable<S3Object> EnumerateRecursive(ZioMountService mount, UPath dir, string prefix)
    {
        foreach (var path in mount.Root.EnumeratePaths(dir, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both))
        {
            var isDir = mount.Root.DirectoryExists(path);
            var key = path.FullName.TrimStart('/');
            if (isDir)
            {
                foreach (var sub in EnumerateRecursive(mount, path, prefix))
                    yield return sub;
            }
            else
            {
                var entry = mount.Root.GetFileEntry(path);
                yield return new S3Object
                {
                    Key = key,
                    Size = entry.Length,
                    LastModified = entry.LastWriteTime.ToUniversalTime(),
                    ETag = $"\"{entry.Length:x}\"",
                    StorageClass = "STANDARD",
                };
            }
        }
    }

    // --- Get object ---

    private static async Task<IResult> GetObjectAsync(
        string bucket,
        string key,
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        var mountPath = "/" + bucket;
        if (!mount.Mounted.ContainsKey(mountPath))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        var upath = UPath.Root / bucket / key;
        if (!mount.Root.FileExists(upath))
            return S3Error("NoSuchKey", "key does not exist", 404);

        var entry = mount.Root.GetFileEntry(upath);
        ctx.Response.Headers["Content-Length"] = entry.Length.ToString();
        ctx.Response.Headers["Content-Type"] = "application/octet-stream";
        ctx.Response.Headers["ETag"] = $"\"{entry.Length:x}\"";
        ctx.Response.Headers["Last-Modified"] = entry.LastWriteTime.ToUniversalTime().ToString("R");

        var stream = mount.Root.OpenFile(upath, FileMode.Open, FileAccess.Read);
        return Results.Stream(stream, "application/octet-stream");
    }

    // --- Head object ---

    private static async Task<IResult> HeadObjectAsync(
        string bucket,
        string key,
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        var mountPath = "/" + bucket;
        if (!mount.Mounted.ContainsKey(mountPath))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        var upath = UPath.Root / bucket / key;
        if (!mount.Root.FileExists(upath))
            return S3Error("NoSuchKey", "key does not exist", 404);

        var entry = mount.Root.GetFileEntry(upath);
        ctx.Response.Headers["Content-Length"] = entry.Length.ToString();
        ctx.Response.Headers["Content-Type"] = "application/octet-stream";
        ctx.Response.Headers["ETag"] = $"\"{entry.Length:x}\"";
        ctx.Response.Headers["Last-Modified"] = entry.LastWriteTime.ToUniversalTime().ToString("R");
        return Results.Ok();
    }

    // --- Put object ---

    private static async Task<IResult> PutObjectAsync(
        string bucket,
        string key,
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        IFileSystemService fs)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);
        if (user.Disabled) return S3Error("AccessDenied", "user disabled", 403);

        var mountPath = "/" + bucket;
        if (!mount.Mounted.ContainsKey(mountPath))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        var path = mountPath + "/" + key;
        await using var stream = ctx.Request.Body;
        var resp = await fs.PutStreamAsync(path, stream, user, ctx.RequestAborted);
        if (resp.Code != 200)
            return S3Error("InternalError", resp.Message, 500);

        var upath = UPath.Root / bucket / key;
        if (mount.Root.FileExists(upath))
        {
            var entry = mount.Root.GetFileEntry(upath);
            ctx.Response.Headers["ETag"] = $"\"{entry.Length:x}\"";
        }
        return Results.Ok();
    }

    // --- Delete object ---

    private static async Task<IResult> DeleteObjectAsync(
        string bucket,
        string key,
        HttpContext ctx,
        ZioMountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        IFileSystemService fs)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);
        if (user.Disabled) return S3Error("AccessDenied", "user disabled", 403);

        var mountPath = "/" + bucket;
        if (!mount.Mounted.ContainsKey(mountPath))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        var path = mountPath + "/" + key;
        var resp = await fs.RemoveAsync([path], user, ctx.RequestAborted);
        if (resp.Code != 200)
            return S3Error("InternalError", resp.Message, 500);

        return Results.NoContent();
    }

    // --- 鉴权：从 JWT 或 SigV4 access key 解析用户 ---

    /// <summary>
    /// 解析请求对应的真实用户。优先采用 JWT 中间件已认证的身份；
    /// 否则从 Authorization 头解析 token（支持 SigV4 Credential、AWS、Bearer、裸 token 四种形态），
    /// 校验后加载真实用户。无效 token 或已禁用用户一律返回 null。（该逻辑封闭了此前
    /// 向后端硬编码 Admin guest 的安全洞。）
    /// </summary>
    private static async Task<User?> ResolveUserAsync(
        HttpContext ctx,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc)
    {
        // 若已通过 JWT 中间件完成认证（ClaimsPrincipal 存在），直接复用
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            return ctx.User.ToUser();
        }

        // 否则尝试从 Authorization 头提取 token：
        //   SigV4: "AWS4-HMAC-SHA256 Credential=<access-key>/..." — 其中 access key 即 JWT token
        //   裸 JWT: 不带 "Bearer " 前缀的 token
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth)) return null;

        string? token = null;
        if (auth.StartsWith("AWS4-HMAC-SHA256", StringComparison.OrdinalIgnoreCase))
        {
            // 从 Credential=<key>/date/region/... 中切出 access key
            var credIdx = auth.IndexOf("Credential=", StringComparison.OrdinalIgnoreCase);
            if (credIdx >= 0)
            {
                var start = credIdx + "Credential=".Length;
                var end = auth.IndexOf(',', start);
                if (end < 0) end = auth.Length;
                var cred = auth[start..end].Trim();
                token = cred.Split('/').FirstOrDefault();
            }
        }
        else if (auth.StartsWith("AWS ", StringComparison.OrdinalIgnoreCase))
        {
            // 旧版 AWS 认证："AWS <access-key>:<signature>"
            token = auth[4..].Split(':').FirstOrDefault();
        }
        else if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = auth[7..];
        }
        else
        {
            // 其余情况把整段 Authorization 当作裸 token
            token = auth;
        }

        if (string.IsNullOrWhiteSpace(token)) return null;

        // 校验 token 并加载真实后置用户，拒绝无效 token / 已禁用用户
        var userId = await authSvc.ValidateTokenAsync(token, ctx.RequestAborted);
        if (userId is null) return null;
        var result = await userSvc.GetAsync(userId.Value, ctx.RequestAborted);
        if (result.Code != RespCodes.Ok) return null;
        var user = result.Data;
        return user is null or { Disabled: true } ? null : user;
    }

    // --- XML helpers ---

    private static IResult XmlResult<T>(T obj)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var sw = new StringWriter();
        using var xw = XmlWriter.Create(sw, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false });
        serializer.Serialize(xw, obj);
        return Results.Text(sw.ToString(), "application/xml", Encoding.UTF8);
    }

    private static IResult S3Error(string code, string message, int status)
    {
        var error = new S3Error
        {
            Code = code,
            Message = message,
            Resource = "",
            RequestId = Guid.NewGuid().ToString("N"),
        };
        var serializer = new XmlSerializer(typeof(S3Error));
        using var sw = new StringWriter();
        using var xw = XmlWriter.Create(sw, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false });
        serializer.Serialize(xw, error);
        return Results.Text(sw.ToString(), "application/xml", Encoding.UTF8, status);
    }
}

// --- S3 XML 响应模型 ---

/// <summary>ListAllMyBucketsResult 响应的根元素（列出桶）。</summary>
[XmlRoot("ListAllMyBucketsResult", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3ListAllMyBucketsResult
{
    public S3Owner Owner { get; set; } = new();
    [XmlElement("Buckets")]
    public S3Buckets BucketsContainer { get; set; } = new();
    [XmlIgnore]
    public List<S3Bucket> Buckets
    {
        get => BucketsContainer.Buckets;
        set => BucketsContainer.Buckets = value;
    }
}

/// <summary>S3 所有者（发送方身份）。</summary>
public class S3Owner
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>桶列表容器。</summary>
public class S3Buckets
{
    [XmlElement("Bucket")]
    public List<S3Bucket> Buckets { get; set; } = [];
}

/// <summary>单个桶（名称 + 创建时间）。</summary>
public class S3Bucket
{
    public string Name { get; set; } = "";
    public DateTime CreationDate { get; set; }
}

/// <summary>ListBucketResult 响应的根元素（列出对象）。</summary>
[XmlRoot("ListBucketResult", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3ListBucketResult
{
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string? Delimiter { get; set; }
    public int MaxKeys { get; set; }
    public bool IsTruncated { get; set; }
    [XmlElement("Contents")]
    public S3Contents ContentsContainer { get; set; } = new();
    [XmlIgnore]
    public List<S3Object> Contents
    {
        get => ContentsContainer.Objects;
        set => ContentsContainer.Objects = value;
    }
    [XmlElement("CommonPrefixes")]
    public S3CommonPrefixes CommonPrefixesContainer { get; set; } = new();
    [XmlIgnore]
    public List<S3CommonPrefix> CommonPrefixes
    {
        get => CommonPrefixesContainer.Prefixes;
        set => CommonPrefixesContainer.Prefixes = value;
    }
}

/// <summary>对象候选集容器（真实对象）。</summary>
public class S3Contents
{
    [XmlElement("Contents")]
    public List<S3Object> Objects { get; set; } = [];
}

/// <summary>公共前缀容器（按 delimiter 分组得到的"目录"项）。</summary>
public class S3CommonPrefixes
{
    [XmlElement("CommonPrefixes")]
    public List<S3CommonPrefix> Prefixes { get; set; } = [];
}

/// <summary>S3 对象（键、大小、ETag、最后修改时间等）。</summary>
public class S3Object
{
    public string Key { get; set; } = "";
    public DateTime LastModified { get; set; }
    public string ETag { get; set; } = "";
    public long Size { get; set; }
    public string StorageClass { get; set; } = "STANDARD";
    public S3Owner Owner { get; set; } = new();
}

/// <summary>公共前缀项。</summary>
public class S3CommonPrefix
{
    public string Prefix { get; set; } = "";
}

/// <summary>S3 错误响应的根元素。</summary>
[XmlRoot("Error", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3Error
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string Resource { get; set; } = "";
    public string RequestId { get; set; } = "";
}
