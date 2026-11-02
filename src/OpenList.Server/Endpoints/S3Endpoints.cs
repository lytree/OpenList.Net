using System.Security.Claims;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Infrastructure.Services;
using OpenList.Server.Services;
using OpenList.Vfs;

namespace OpenList.Server.Endpoints;

/// <summary>
/// S3 兼容网关端点，路由位于 <c>/s3</c>。
/// 将 S3 桶（bucket）映射到挂载路径、对象键（key）映射到存储内的文件路径。
/// 对应 OpenList Go <c>server/s3</c>。
/// <para>
/// 除基础的对象读写外，还实现了分片上传（Multipart Upload：init/part/complete/abort/list）、
/// ListObjectsV2 分页、服务端复制（x-amz-copy-source）、批量删除（POST ?delete）与 Range 请求。
/// </para>
/// 鉴权：既接受 OpenList JWT（Authorization 头不带 "Bearer "），也接受 AWS SigV4
/// （Authorization: AWS4-HMAC-SHA256 ...），SigV4 的 access key 被当作 JWT 使用。
/// 需要先在中间件/此处完成令牌校验后的真实用户解析，否则一律 401。
/// </summary>
public static class S3Endpoints
{
    /// <summary>
    /// 注册 <c>/s3</c> 路由组。<c>/{bucket}[/{*key}]</c> 基线路由支持
    /// GET(列桶/列对象/取对象)、HEAD(桶/对象头)、PUT(上传/分片上传/复制)、
    /// POST(分片上传 init/complete、批量删除)、DELETE(删除/中止分片上传)。
    /// 无需 JWT 中间件（DisableAntiforgery），由 <see cref="ResolveUserAsync"/>
    /// 从 JWT 或 SigV4 头自行解析用户。
    /// </summary>
    public static IEndpointRouteBuilder MapS3Api(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/s3").WithTags("S3").DisableAntiforgery();

        // GET /s3 — 列出所有桶（对应挂载的存储）
        g.MapGet("/", ListBucketsAsync);

        // GET /s3/{bucket} — 列出桶内对象（V1/V2）/ 列出进行中的分片上传（?uploads）
        g.MapGet("/{bucket}", ListBucketDispatchAsync);

        // HEAD /s3/{bucket} — 探测桶是否存在
        g.MapMethods("/{bucket}", ["HEAD"], HeadBucketAsync);

        // POST /s3/{bucket}?delete — 批量删除对象（DeleteObjects）
        g.MapPost("/{bucket}", PostBucketAsync);

        // GET /s3/{bucket}/{*key} — 获取对象（即下载文件，支持 Range）
        g.MapGet("/{bucket}/{*key}", GetObjectAsync);

        // HEAD /s3/{bucket}/{*key} — 获取对象元数据（仅响应头，不返回体）
        g.MapMethods("/{bucket}/{*key}", ["HEAD"], HeadObjectAsync);

        // PUT /s3/{bucket}/{*key} — 上传对象 / 上传分片(?partNumber&uploadId) / 服务端复制(x-amz-copy-source)
        g.MapPut("/{bucket}/{*key}", PutObjectDispatchAsync);

        // POST /s3/{bucket}/{*key}?uploads — 初始化分片上传；?uploadId=X — 完成分片上传
        g.MapPost("/{bucket}/{*key}", PostObjectAsync);

        // DELETE /s3/{bucket}/{*key} — 删除对象 / 中止分片上传(?uploadId)
        g.MapDelete("/{bucket}/{*key}", DeleteObjectDispatchAsync);

        return app;
    }

    // --- 列出桶 ---

    /// <summary>GET /s3 — 列出所有已启用存储对应的桶并以 S3 ListAllMyBuckets 格式返回。</summary>
    private static async Task<IResult> ListBucketsAsync(
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        // 桶可见范围 = 挂载点与用户 BasePath 的交集。受限用户只应看到自己根目录内的挂载点，
        // 名字也按自己的根目录换算（/a/b1 对 BasePath=/a 的用户就是 b1）。
        var basePath = MetaAccess.Normalize(string.IsNullOrEmpty(user.BasePath) ? "/" : user.BasePath);
        var storages = await storageSvc.ListAsync(ctx.RequestAborted);
        var buckets = storages.Data?.Content
            .Where(s => !s.Disabled && MetaAccess.IsSubPath(basePath, s.MountPath))
            .Select(s => new S3Bucket
            {
                Name = RelativeBucketName(basePath, s.MountPath),
                CreationDate = s.CreatedAt.UtcDateTime,
            })
            .Where(b => !string.IsNullOrEmpty(b.Name))
            .ToList() ?? [];

        var result = new S3ListAllMyBucketsResult
        {
            Owner = new S3Owner { Id = user.Username, DisplayName = user.Username },
            Buckets = buckets,
        };

        return XmlResult(result);
    }

    // --- 桶解析 ---

    /// <summary>
    /// 把 S3 bucket 名解析为当前用户可访问的挂载路径。
    /// bucket 名是**相对于用户 BasePath** 的路径：BasePath=/a 的用户请求 <c>b1</c> 得到 /a/b1。
    /// 不存在与越界都返回 false，调用方统一回 NoSuchBucket —— 区分开等于确认「桶存在但你没权限」。
    /// </summary>
    private static bool TryResolveBucket(User user, MountService mount, string bucket, out string bucketRoot)
    {
        bucketRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(bucket)) return false;
        if (!user.TryJoinPath("/" + bucket.Trim('/'), out var joined)) return false;
        if (!mount.Mounted.ContainsKey(joined)) return false;
        bucketRoot = joined;
        return true;
    }

    /// <summary>把挂载路径换算成用户视角的桶名（去掉自己的 BasePath 前缀）。</summary>
    private static string RelativeBucketName(string basePath, string mountPath)
    {
        var name = MetaAccess.Normalize(mountPath).Trim('/');
        if (basePath == "/") return name;
        var bp = basePath.Trim('/');
        if (string.Equals(name, bp, StringComparison.Ordinal)) return string.Empty;
        return name.StartsWith(bp + "/", StringComparison.Ordinal) ? name[(bp.Length + 1)..] : name;
    }

    // --- Head bucket ---

    private static async Task<IResult> HeadBucketAsync(
        string bucket,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        if (!TryResolveBucket(user, mount, bucket, out _))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        ctx.Response.Headers["x-amz-bucket-region"] = "us-east-1";
        return Results.Ok();
    }

    // --- 列出对象 / 列出分片上传（按查询参数分派） ---

    private static async Task<IResult> ListBucketDispatchAsync(
        string bucket,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        S3MultipartStore multipart,
        FileAccessService access)
    {
        // ?uploads — 列出该桶进行中的分片上传
        if (ctx.Request.Query.ContainsKey("uploads"))
        {
            var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
            if (user is null) return S3Error("AccessDenied", "unauthorized", 401);
            if (!TryResolveBucket(user, mount, bucket, out _))
                return S3Error("NoSuchBucket", "bucket does not exist", 404);
            return ListMultipartUploads(bucket, ctx, multipart, user);
        }

        return await ListObjectsAsync(bucket, ctx, mount, storageSvc, authSvc, userSvc, access);
    }

    private static IResult ListMultipartUploads(string bucket, HttpContext ctx, S3MultipartStore store, User user)
    {
        var prefix = ctx.Request.Query["prefix"].ToString();
        // 只列自己的会话：分片会话的 uploadId 对同站点其他用户不可见。
        var uploads = store.List(bucket, user.Id)
            .Where(u => string.IsNullOrEmpty(prefix) || u.Object.StartsWith(prefix, StringComparison.Ordinal))
            .Select(u => new S3MultipartUpload
            {
                Key = u.Object,
                UploadId = u.UploadId,
                Initiator = new S3Owner { Id = user.Username, DisplayName = user.Username },
                Owner = new S3Owner { Id = user.Username, DisplayName = user.Username },
                StorageClass = "STANDARD",
                Initiated = u.Initiated.UtcDateTime,
            })
            .ToList();

        return XmlResult(new S3ListMultipartUploadsResult
        {
            Bucket = bucket,
            Prefix = prefix,
            MaxUploads = 1000,
            IsTruncated = false,
            Uploads = uploads,
        });
    }

    private static async Task<IResult> ListObjectsAsync(
        string bucket,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        if (!TryResolveBucket(user, mount, bucket, out var bucketRoot))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        // 桶根的读门禁：ReadUsers / 隐藏 / 目录密码同样约束 S3，否则 meta 只挡住了 /api/fs。
        var decision = await access.AuthorizeAsync(user, bucketRoot, FileOperation.Read, null, ctx.RequestAborted);
        if (!decision.Allowed) return S3Error("AccessDenied", decision.Message, 403);

        var query = ctx.Request.Query;
        var isV2 = query["list-type"].ToString() == "2";
        var prefix = query["prefix"].ToString();
        var delimiter = query["delimiter"].ToString();
        var maxKeys = int.TryParse(query["max-keys"], out var mk) && mk > 0 ? mk : 1000;
        var continuationToken = isV2 ? query["continuation-token"].ToString() : "";
        var startAfter = isV2 ? query["start-after"].ToString() : "";
        var marker = isV2 ? "" : query["marker"].ToString();
        var resumeAfter = !string.IsNullOrEmpty(startAfter) ? startAfter
            : !string.IsNullOrEmpty(continuationToken) ? continuationToken
            : marker;

        // 桶根 = 该用户视角下的挂载点。此前用 UPath.Root 作搜索基址，等于把**整个站点根**
        // 当成桶内容枚举：任何能读一个桶的用户都能看到其它挂载点下的文件。
        var bucketRootPath = new UPath(bucketRoot);
        var searchBase = string.IsNullOrEmpty(prefix) ? bucketRootPath : (bucketRootPath / prefix.TrimEnd('/'));

        var all = new List<S3Object>();
        var commonPrefixes = new List<string>();

        try
        {
            if (mount.Root.DirectoryExists(searchBase))
            {
                var keyPrefix = bucketRootPath.FullName.TrimEnd('/') + "/";
                // meta 只取一次：逐项查库会让一次列桶变成 N 次查询。
                var metas = await access.LoadMetasAsync(ctx.RequestAborted);
                foreach (var path in EnumerateFiles(mount, searchBase))
                {
                    // 逐项可见性：父目录可见不代表嵌套子项可见（隐藏规则按各自父目录匹配）。
                    if (!access.CanSee(user, path.FullName, null, metas)) continue;
                    var entry = mount.Root.GetFileEntry(path);
                    var full = path.FullName;
                    all.Add(new S3Object
                    {
                        // S3 的 key 是桶内相对路径，不能把挂载前缀暴露给客户端。
                        Key = full.StartsWith(keyPrefix, StringComparison.Ordinal) ? full[keyPrefix.Length..] : full.TrimStart('/'),
                        Size = entry.Length,
                        LastModified = entry.LastWriteTime.ToUniversalTime(),
                        ETag = $"\"{entry.Length:x}\"",
                        StorageClass = "STANDARD",
                    });
                }
                // 稳定排序，分页游标才有意义
                all.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            }
        }
        catch (Exception ex)
        {
            return S3Error("InternalError", ex.Message, 500);
        }

        // 续传游标：跳过键值不晚于 resumeAfter 的条目
        if (!string.IsNullOrEmpty(resumeAfter))
            all = all.Where(o => string.CompareOrdinal(o.Key, resumeAfter) > 0).ToList();

        var truncated = false;
        if (all.Count > maxKeys)
        {
            all = all.Take(maxKeys).ToList();
            truncated = true;
        }

        if (delimiter == "/")
        {
            // 按搜索基址下的一级前缀分组，把"目录"归入 CommonPrefixes
            var seen = new HashSet<string>();
            var kept = new List<S3Object>();
            foreach (var c in all)
            {
                var rel = c.Key.StartsWith(prefix) ? c.Key[prefix.Length..] : c.Key;
                var slashIdx = rel.IndexOf('/');
                if (slashIdx < 0) { kept.Add(c); continue; }
                var cp = prefix + rel[..(slashIdx + 1)];
                if (seen.Add(cp)) commonPrefixes.Add(cp);
            }
            all = kept;
        }

        var result = new S3ListBucketResult
        {
            Name = bucket,
            Prefix = prefix,
            Delimiter = string.IsNullOrEmpty(delimiter) ? null : delimiter,
            MaxKeys = maxKeys,
            IsTruncated = truncated,
            Contents = all,
            CommonPrefixes = commonPrefixes.Select(p => new S3CommonPrefix { Prefix = p }).ToList(),
        };

        if (isV2)
        {
            result.KeyCount = all.Count;
            result.ContinuationToken = string.IsNullOrEmpty(continuationToken) ? null : continuationToken;
            result.StartAfter = string.IsNullOrEmpty(startAfter) ? null : startAfter;
            result.NextContinuationToken = truncated && all.Count > 0 ? all[^1].Key : null;
        }
        else
        {
            result.Marker = string.IsNullOrEmpty(marker) ? null : marker;
            result.NextMarker = truncated && all.Count > 0 ? all[^1].Key : null;
        }

        return XmlResult(result);
    }

    /// <summary>递归枚举目录下的所有**文件**路径（不含目录本身），供 ListObjects 逐项做可见性判定。</summary>
    private static IEnumerable<UPath> EnumerateFiles(MountService mount, UPath dir)
    {
        foreach (var path in mount.Root.EnumeratePaths(dir, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both))
        {
            if (mount.Root.DirectoryExists(path))
            {
                foreach (var sub in EnumerateFiles(mount, path))
                    yield return sub;
            }
            else
            {
                yield return path;
            }
        }
    }

    // --- Get object ---

    private static async Task<IResult> GetObjectAsync(
        string bucket,
        string key,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        if (!TryResolveBucket(user, mount, bucket, out var bucketRoot))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        var upath = new UPath(bucketRoot) / key;
        var decision = await access.AuthorizeAsync(user, upath.FullName, FileOperation.Read, null, ctx.RequestAborted);
        if (!decision.Allowed) return S3Error("AccessDenied", decision.Message, 403);
        if (!mount.Root.FileExists(upath))
            return S3Error("NoSuchKey", "key does not exist", 404);

        var entry = mount.Root.GetFileEntry(upath);
        ctx.Response.Headers["Content-Type"] = "application/octet-stream";
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.Headers["ETag"] = $"\"{entry.Length:x}\"";
        ctx.Response.Headers["Last-Modified"] = entry.LastWriteTime.ToUniversalTime().ToString("R");

        // Range 请求：返回 206 + Content-Range（对应 Go server/s3/range.go 的意图）
        var rangeHeader = ctx.Request.Headers.Range.ToString();
        if (entry.Length > 0 && !string.IsNullOrEmpty(rangeHeader)
            && TryParseRange(rangeHeader, entry.Length, out var start, out var end))
        {
            var count = end - start + 1;
            var ranged = CreateRangeStream(mount.Root.OpenFile(upath, FileMode.Open, FileAccess.Read), start, count);
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{entry.Length}";
            ctx.Response.Headers["Content-Length"] = count.ToString();
            return Results.Stream(ranged, "application/octet-stream");
        }

        ctx.Response.Headers["Content-Length"] = entry.Length.ToString();
        var stream = mount.Root.OpenFile(upath, FileMode.Open, FileAccess.Read);
        return Results.Stream(stream, "application/octet-stream");
    }

    // --- Head object ---

    private static async Task<IResult> HeadObjectAsync(
        string bucket,
        string key,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);

        if (!TryResolveBucket(user, mount, bucket, out var bucketRoot))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        var upath = new UPath(bucketRoot) / key;
        var decision = await access.AuthorizeAsync(user, upath.FullName, FileOperation.Read, null, ctx.RequestAborted);
        if (!decision.Allowed) return S3Error("AccessDenied", decision.Message, 403);
        if (!mount.Root.FileExists(upath))
            return S3Error("NoSuchKey", "key does not exist", 404);

        var entry = mount.Root.GetFileEntry(upath);
        ctx.Response.Headers["Content-Length"] = entry.Length.ToString();
        ctx.Response.Headers["Content-Type"] = "application/octet-stream";
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.Headers["ETag"] = $"\"{entry.Length:x}\"";
        ctx.Response.Headers["Last-Modified"] = entry.LastWriteTime.ToUniversalTime().ToString("R");
        return Results.Ok();
    }

    // --- Put object（分派：分片 / 复制 / 普通上传） ---

    private static async Task<IResult> PutObjectDispatchAsync(
        string bucket,
        string key,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        IFileSystemService fs,
        S3MultipartStore multipart,
        FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);
        if (user.Disabled) return S3Error("AccessDenied", "user disabled", 403);

        if (!TryResolveBucket(user, mount, bucket, out var bucketRoot))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        // UploadPart：?partNumber=N&uploadId=X
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        if (!string.IsNullOrEmpty(uploadId))
        {
            if (!int.TryParse(ctx.Request.Query["partNumber"].ToString(), out var partNumber))
                return S3Error("InvalidArgument", "partNumber is required", 400);

            var result = await multipart.UploadPartAsync(
                uploadId, partNumber, ctx.Request.Body, ctx.Request.ContentLength, user.Id, ctx.RequestAborted);
            if (!result.Success)
                return S3Error(result.ErrorCode ?? "InternalError", result.ErrorMessage ?? "upload part failed",
                    MapStatus(result.ErrorCode));

            ctx.Response.Headers["ETag"] = result.ETag;
            return Results.Ok();
        }

        // CopyObject：x-amz-copy-source: /srcBucket/srcKey
        var copySource = ctx.Request.Headers["x-amz-copy-source"].ToString();
        if (!string.IsNullOrEmpty(copySource))
            return await CopyObjectAsync(copySource, bucketRoot, key, ctx, mount, user, fs, access);

        return await PutObjectCoreAsync(bucketRoot, key, ctx, mount, user, fs, access);
    }

    private static async Task<IResult> PutObjectCoreAsync(
        string bucketRoot,
        string key,
        HttpContext ctx,
        MountService mount,
        User user,
        IFileSystemService fs,
        FileAccessService access)
    {
        var path = bucketRoot.TrimEnd('/') + "/" + key.TrimStart('/');

        // 写入目标父目录要过「可写内容 + WriteUsers」门禁。此前 S3 PUT 完全不做授权，
        // 任何持有 token 的用户都能往任意挂载点写文件。
        var parent = MetaAccess.Parent(path);
        var decision = await access.AuthorizeAsync(user, parent, FileOperation.ContentWrite, null, ctx.RequestAborted);
        if (!decision.Allowed) return S3Error("AccessDenied", decision.Message, 403);

        await using var stream = ctx.Request.Body;
        var resp = await fs.PutStreamAsync(path, stream, user, ctx.RequestAborted);
        if (resp.Code != 200)
            return S3Error("InternalError", resp.Message, 500);

        var upath = new UPath(path);
        if (mount.Root.FileExists(upath))
        {
            var entry = mount.Root.GetFileEntry(upath);
            ctx.Response.Headers["ETag"] = $"\"{entry.Length:x}\"";
        }
        return Results.Ok();
    }

    private static async Task<IResult> CopyObjectAsync(
        string copySource,
        string destBucketRoot,
        string destKey,
        HttpContext ctx,
        MountService mount,
        User user,
        IFileSystemService fs,
        FileAccessService access)
    {
        // x-amz-copy-source 形如 "/srcBucket/srcKey" 或 "srcBucket/srcKey"（可能 URL 编码）
        var unescaped = Uri.UnescapeDataString(copySource.TrimStart('/'));
        var slash = unescaped.IndexOf('/');
        if (slash <= 0) return S3Error("InvalidArgument", "invalid copy source", 400);

        var srcBucket = unescaped[..slash];
        var srcKey = unescaped[(slash + 1)..];
        // 源桶也要受 BasePath 约束：否则可以把别人挂载点当复制源读出来。
        if (!TryResolveBucket(user, mount, srcBucket, out var srcBucketRoot))
            return S3Error("NoSuchBucket", "source bucket does not exist", 404);

        var srcPath = new UPath(srcBucketRoot) / srcKey;
        var readDecision = await access.AuthorizeAsync(user, srcPath.FullName, FileOperation.Read, null, ctx.RequestAborted);
        if (!readDecision.Allowed) return S3Error("AccessDenied", readDecision.Message, 403);
        if (!mount.Root.FileExists(srcPath))
            return S3Error("NoSuchKey", "source key does not exist", 404);

        var destPath = destBucketRoot.TrimEnd('/') + "/" + destKey.TrimStart('/');
        var writeDecision = await access.AuthorizeAsync(
            user, MetaAccess.Parent(destPath), FileOperation.ContentWrite, null, ctx.RequestAborted);
        if (!writeDecision.Allowed) return S3Error("AccessDenied", writeDecision.Message, 403);

        await using (var src = mount.Root.OpenFile(srcPath, FileMode.Open, FileAccess.Read))
        {
            var resp = await fs.PutStreamAsync(destPath, src, user, ctx.RequestAborted);
            if (resp.Code != 200)
                return S3Error("InternalError", resp.Message, 500);
        }

        var entry = mount.Root.GetFileEntry(destPath);
        return XmlResult(new S3CopyObjectResult
        {
            LastModified = entry.LastWriteTime.ToUniversalTime(),
            ETag = $"\"{entry.Length:x}\"",
        });
    }

    // --- Post object（分片上传 init / complete） ---

    private static async Task<IResult> PostObjectAsync(
        string bucket,
        string key,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        IFileSystemService fs,
        S3MultipartStore multipart,
        FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);
        if (user.Disabled) return S3Error("AccessDenied", "user disabled", 403);

        if (!TryResolveBucket(user, mount, bucket, out var bucketRoot))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        var targetPath = bucketRoot.TrimEnd('/') + "/" + key.TrimStart('/');
        // 目标父目录必须可写：分片会话的创建与完成都会最终落盘到这个路径。
        var writeDecision = await access.AuthorizeAsync(
            user, MetaAccess.Parent(targetPath), FileOperation.ContentWrite, null, ctx.RequestAborted);
        if (!writeDecision.Allowed) return S3Error("AccessDenied", writeDecision.Message, 403);

        // CompleteMultipartUpload：?uploadId=X
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        if (!string.IsNullOrEmpty(uploadId))
        {
            if (!multipart.IsOwner(uploadId, user.Id))
                return S3Error("NoSuchUpload", "upload does not exist", 404);

            var parts = await ReadCompleteRequestAsync(ctx);
            var prepared = multipart.PrepareComplete(uploadId, parts, user.Id);
            if (!prepared.Success)
                return S3Error(prepared.ErrorCode ?? "InternalError", prepared.ErrorMessage ?? "complete failed",
                    MapStatus(prepared.ErrorCode));

            try
            {
                var resp = await fs.PutStreamAsync(targetPath, prepared.Stream!, user, ctx.RequestAborted);
                if (resp.Code != 200)
                    return S3Error("InternalError", resp.Message, 500);
            }
            finally
            {
                await prepared.Stream!.DisposeAsync();
            }

            // 成功后清理分片与临时目录
            multipart.Remove(uploadId);

            return XmlResult(new S3CompleteMultipartUploadResult
            {
                Location = $"/{bucket}/{key}",
                Bucket = bucket,
                Key = key,
                ETag = prepared.ETag,
            });
        }

        // CreateMultipartUpload：?uploads
        if (ctx.Request.Query.ContainsKey("uploads"))
        {
            var newId = multipart.CreateUpload(bucket, key, user.Id);
            return XmlResult(new S3InitiateMultipartUploadResult { Bucket = bucket, Key = key, UploadId = newId });
        }

        return S3Error("NotImplemented", "unsupported object POST operation", 501);
    }

    // --- Post bucket（批量删除） ---

    private static async Task<IResult> PostBucketAsync(
        string bucket,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        IFileSystemService fs,
        FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);
        if (user.Disabled) return S3Error("AccessDenied", "user disabled", 403);

        if (!TryResolveBucket(user, mount, bucket, out var bucketRoot))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        if (!ctx.Request.Query.ContainsKey("delete"))
            return S3Error("NotImplemented", "unsupported bucket POST operation", 501);

        // 桶根必须可写（WriteUsers）；逐项删除还要过各自的父目录门禁。
        var rootDecision = await access.AuthorizeAsync(user, bucketRoot, FileOperation.MetaWrite, null, ctx.RequestAborted);
        if (!rootDecision.Allowed) return S3Error("AccessDenied", rootDecision.Message, 403);

        var keys = await ReadDeleteRequestAsync(ctx);
        var result = new S3DeleteResult();
        foreach (var k in keys)
        {
            var path = bucketRoot.TrimEnd('/') + "/" + k.TrimStart('/');
            var decision = await access.AuthorizeAsync(
                user, MetaAccess.Parent(path), FileOperation.MetaWrite, null, ctx.RequestAborted);
            var resp = decision.Allowed
                ? await fs.RemoveAsync([path], user, ctx.RequestAborted)
                : Resp.Fail(decision.Message, decision.Code);
            if (resp.Code == 200)
            {
                result.Deleted.Add(new S3DeletedObject { Key = k });
            }
            else
            {
                result.Errors.Add(new S3Error
                {
                    Code = resp.Code == RespCodes.Forbidden ? "AccessDenied" : "InternalError",
                    Message = resp.Message,
                    Resource = k,
                    RequestId = Guid.NewGuid().ToString("N"),
                });
            }
        }
        return XmlResult(result);
    }

    // --- Delete object（分派：中止分片 / 删除对象） ---

    private static async Task<IResult> DeleteObjectDispatchAsync(
        string bucket,
        string key,
        HttpContext ctx,
        MountService mount,
        IStorageService storageSvc,
        IAuthService authSvc,
        IUserService userSvc,
        IFileSystemService fs,
        S3MultipartStore multipart,
        FileAccessService access)
    {
        var user = await ResolveUserAsync(ctx, storageSvc, authSvc, userSvc);
        if (user is null) return S3Error("AccessDenied", "unauthorized", 401);
        if (user.Disabled) return S3Error("AccessDenied", "user disabled", 403);

        if (!TryResolveBucket(user, mount, bucket, out var bucketRoot))
            return S3Error("NoSuchBucket", "bucket does not exist", 404);

        // AbortMultipartUpload：?uploadId=X（幂等）
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        if (!string.IsNullOrEmpty(uploadId))
        {
            // 只能中止自己的会话（不属于自己时保持幂等语义：直接 NoContent 会掩盖探测，
            // 这里统一按 NoSuchUpload 拒绝）。
            if (!multipart.IsOwner(uploadId, user.Id))
                return S3Error("NoSuchUpload", "upload does not exist", 404);
            multipart.Remove(uploadId);
            return Results.NoContent();
        }

        var path = bucketRoot.TrimEnd('/') + "/" + key.TrimStart('/');
        var decision = await access.AuthorizeAsync(
            user, MetaAccess.Parent(path), FileOperation.MetaWrite, null, ctx.RequestAborted);
        if (!decision.Allowed) return S3Error("AccessDenied", decision.Message, 403);

        var resp = await fs.RemoveAsync([path], user, ctx.RequestAborted);
        if (resp.Code != 200)
            return S3Error("InternalError", resp.Message, 500);

        return Results.NoContent();
    }

    // --- 请求体解析 ---

    /// <summary>
    /// 异步读取并解析 XML 请求体（忽略元素命名空间）。
    /// 注意：必须异步读取 —— ASP.NET Core 默认禁止同步 IO（AllowSynchronousIO=false），
    /// 直接 <c>XmlDocument.Load(Request.Body)</c> 会抛 InvalidOperationException。
    /// </summary>
    private static async Task<XmlDocument?> ReadXmlBodyAsync(HttpContext ctx)
    {
        try
        {
            using var reader = new StreamReader(
                ctx.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var xml = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(xml)) return null;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 CompleteMultipartUpload 请求体（忽略命名空间）。</summary>
    private static async Task<List<(int PartNumber, string ETag)>> ReadCompleteRequestAsync(HttpContext ctx)
    {
        var parts = new List<(int, string)>();
        var doc = await ReadXmlBodyAsync(ctx);
        if (doc is null) return parts;

        var nodes = doc.SelectNodes("//*[local-name()='Part']");
        if (nodes is not null)
        {
            foreach (XmlNode n in nodes)
            {
                var numText = n.SelectSingleNode("*[local-name()='PartNumber']")?.InnerText;
                var etag = n.SelectSingleNode("*[local-name()='ETag']")?.InnerText ?? "";
                if (int.TryParse(numText, out var num))
                    parts.Add((num, etag));
            }
        }
        return parts;
    }

    /// <summary>解析 DeleteObjects（批量删除）请求体，返回待删除的 key 列表。</summary>
    private static async Task<List<string>> ReadDeleteRequestAsync(HttpContext ctx)
    {
        var keys = new List<string>();
        var doc = await ReadXmlBodyAsync(ctx);
        if (doc is null) return keys;

        var nodes = doc.SelectNodes("//*[local-name()='Object']/*[local-name()='Key']");
        if (nodes is not null)
        {
            foreach (XmlNode n in nodes)
            {
                if (!string.IsNullOrEmpty(n.InnerText)) keys.Add(n.InnerText);
            }
        }
        return keys;
    }

    // --- Range 辅助 ---

    /// <summary>解析单区间 Range 头（<c>bytes=start-end</c> / <c>bytes=start-</c> / <c>bytes=-suffix</c>）。</summary>
    private static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (length <= 0) return false;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;

        var startStr = spec[..dash];
        var endStr = spec[(dash + 1)..];

        if (startStr.Length == 0)
        {
            if (!long.TryParse(endStr, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
            end = length - 1;
        }
        else
        {
            if (!long.TryParse(startStr, out start)) return false;
            if (endStr.Length > 0)
            {
                if (!long.TryParse(endStr, out end)) return false;
            }
            else
            {
                end = length - 1;
            }
            if (start > end || start >= length) return false;
            if (end >= length) end = length - 1;
        }
        return true;
    }

    private static Stream CreateRangeStream(Stream inner, long start, long count)
    {
        if (inner.CanSeek)
        {
            inner.Seek(start, SeekOrigin.Begin);
        }
        else
        {
            var buffer = new byte[81920];
            var toSkip = start;
            while (toSkip > 0)
            {
                var n = inner.Read(buffer, 0, (int)Math.Min(buffer.Length, toSkip));
                if (n <= 0) break;
                toSkip -= n;
            }
        }
        return new RangeStream(inner, count);
    }

    private static int MapStatus(string? code) => code switch
    {
        "NoSuchUpload" or "NoSuchBucket" or "NoSuchKey" => 404,
        "AccessDenied" => 403,
        "InvalidPart" or "InvalidPartOrder" or "InvalidArgument" or "MalformedXML" or "IncompleteBody" => 400,
        _ => 500,
    };

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

    /// <summary>只读的范围流：从底层流读取固定字节数（用于 206 响应）。</summary>
    private sealed class RangeStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public RangeStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, _remaining);
            var n = _inner.Read(buffer, offset, toRead);
            _remaining -= n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var toRead = (int)Math.Min(buffer.Length, _remaining);
            var n = await _inner.ReadAsync(buffer[..toRead], cancellationToken);
            _remaining -= n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
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

/// <summary>ListBucketResult 响应的根元素（列出对象，同时兼容 V1 与 V2）。</summary>
[XmlRoot("ListBucketResult", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3ListBucketResult
{
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string? Delimiter { get; set; }
    public int MaxKeys { get; set; }
    public bool IsTruncated { get; set; }

    /// <summary>V1 续传游标（回显请求中的 marker）。</summary>
    public string? Marker { get; set; }
    /// <summary>V1 下一页游标。</summary>
    public string? NextMarker { get; set; }

    /// <summary>V2 返回的对象总数。</summary>
    public int? KeyCount { get; set; }
    /// <summary>V2 续传令牌（回显请求中的 continuation-token）。</summary>
    public string? ContinuationToken { get; set; }
    /// <summary>V2 下一页续传令牌。</summary>
    public string? NextContinuationToken { get; set; }
    /// <summary>V2 start-after 回显。</summary>
    public string? StartAfter { get; set; }

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

/// <summary>InitiateMultipartUploadResult 响应的根元素。</summary>
[XmlRoot("InitiateMultipartUploadResult", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3InitiateMultipartUploadResult
{
    public string Bucket { get; set; } = "";
    public string Key { get; set; } = "";
    public string UploadId { get; set; } = "";
}

/// <summary>CompleteMultipartUploadResult 响应的根元素。</summary>
[XmlRoot("CompleteMultipartUploadResult", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3CompleteMultipartUploadResult
{
    public string Location { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Key { get; set; } = "";
    public string ETag { get; set; } = "";
}

/// <summary>ListMultipartUploadsResult 响应的根元素。</summary>
[XmlRoot("ListMultipartUploadsResult", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3ListMultipartUploadsResult
{
    public string Bucket { get; set; } = "";
    public string KeyMarker { get; set; } = "";
    public string UploadIdMarker { get; set; } = "";
    public string NextKeyMarker { get; set; } = "";
    public string NextUploadIdMarker { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string? Delimiter { get; set; }
    public int MaxUploads { get; set; } = 1000;
    public bool IsTruncated { get; set; }
    [XmlElement("Upload")]
    public List<S3MultipartUpload> Uploads { get; set; } = [];
}

/// <summary>单个进行中的分片上传（ListMultipartUploads 的条目）。</summary>
public class S3MultipartUpload
{
    public string Key { get; set; } = "";
    public string UploadId { get; set; } = "";
    public S3Owner Initiator { get; set; } = new();
    public S3Owner Owner { get; set; } = new();
    public string StorageClass { get; set; } = "STANDARD";
    public DateTime Initiated { get; set; }
}

/// <summary>CopyObjectResult 响应的根元素。</summary>
[XmlRoot("CopyObjectResult", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3CopyObjectResult
{
    public DateTime LastModified { get; set; }
    public string ETag { get; set; } = "";
}

/// <summary>DeleteResult 响应的根元素（批量删除）。</summary>
[XmlRoot("DeleteResult", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class S3DeleteResult
{
    [XmlElement("Deleted")]
    public List<S3DeletedObject> Deleted { get; set; } = [];
    [XmlElement("Error")]
    public List<S3Error> Errors { get; set; } = [];
}

/// <summary>已删除的对象。</summary>
public class S3DeletedObject
{
    public string Key { get; set; } = "";
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
