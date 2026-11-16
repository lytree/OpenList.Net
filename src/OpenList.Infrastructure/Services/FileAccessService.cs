using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Core.Security;
using OpenList.Vfs;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 文件操作类型。<see cref="FileAccessService"/> 按操作决定需要哪些门禁，
/// 使 <c>/api/fs</c>、WebDAV、S3、FTP、分片上传、任务与离线下载共用同一套判定。
/// </summary>
public enum FileOperation
{
    /// <summary>读取（列目录、取元信息、下载、归档内条目预览）。</summary>
    Read,
    /// <summary>写入内容（上传、建目录、解压落盘、离线下载目标）。仅需已登录。</summary>
    ContentWrite,
    /// <summary>仅元数据写入（重命名/移动/复制/删除）。仅需已登录。</summary>
    MetaWrite,
}

/// <summary>
/// 授权结果。刻意不返回 <c>IResult</c>：同一个判定要同时服务于
/// <c>HTTP 200 + Resp</c>（/api/fs）、真实状态码（WebDAV）、XML 错误（S3）与 FTP 错误码，
/// 由各协议在边界处自行映射。
/// </summary>
public readonly record struct AccessDecision(bool Allowed, int Code, string Message)
{
    /// <summary>放行。</summary>
    public static AccessDecision Allow => new(true, RespCodes.Ok, "");

    /// <summary>拒绝，并给出业务码与可读原因。</summary>
    public static AccessDecision Deny(int code, string message) => new(false, code, message);
}

/// <summary>
/// 协议无关的文件访问授权服务（scoped：依赖 scoped 的 <see cref="IMetaService"/>）。
/// <para>
/// 背景：授权此前只存在于 <c>FsEndpoints</c> 里，WebDAV / S3 / FTP / 分片上传 / 任务各自
/// 直接调用 <see cref="MountService"/> 或 <see cref="IFileSystemService"/>，于是出现
/// 「/api/fs 挡住了，别的协议没挡」的越权面。这里把「路径解析 → 读/写门禁」收敛成一条主线，
/// 具体判定仍复用 <see cref="MetaAccess"/> 的纯函数，避免出现第二份语义。
/// </para>
/// <para>
/// 单用户产品：读取只受隐藏规则与目录密码约束（匿名访客受限）；写入只要已登录即可。
/// <see cref="FileSystemService"/> 是单例，访问 meta 必须经由本服务（scoped）而不是把
/// <see cref="IMetaService"/> 注入单例。
/// </para>
/// </summary>
public sealed class FileAccessService
{
    private readonly IMetaService _meta;

    /// <summary>创建授权服务。</summary>
    /// <param name="meta">元数据服务（scoped）。</param>
    public FileAccessService(IMetaService meta) => _meta = meta;

    /// <summary>
    /// 把请求路径规整为完整的虚拟路径（补前导斜杠、解析 <c>.</c>/<c>..</c>）。
    /// </summary>
    public static bool TryResolvePath(User user, string? requestedPath, out string fullPath)
    {
        fullPath = UPath.FixAndCleanPath(requestedPath);
        return true;
    }

    /// <summary>解析路径的最近 meta（镜像 Go <c>op.GetNearestMeta</c>）；失败返回 null。</summary>
    public async Task<Meta?> NearestMetaAsync(string path, CancellationToken ct = default)
    {
        try { return MetaAccess.FindNearest((await _meta.ListAsync(ct)).Data ?? [], path); }
        catch { return null; }
    }

    /// <summary>
    /// 一次性拉取 meta 列表。列目录（PROPFIND / S3 ListObjects / FTP LIST）里逐项判定
    /// 若每项都重新查库，条目一多就是 N 次查询；这类批量场景应先取一次再逐项判定。
    /// </summary>
    public async Task<IReadOnlyList<Meta>> LoadMetasAsync(CancellationToken ct = default)
    {
        try { return (await _meta.ListAsync(ct)).Data ?? []; }
        catch { return []; }
    }

    /// <summary>
    /// 对已解析的完整路径做授权判定。
    /// <para>
    /// 读取：<see cref="MetaAccess.CanAccess"/>（隐藏条目 → 免密 → 目录密码）。
    /// 写入：只要已登录（<see cref="User.IsAdmin"/>）即放行。
    /// </para>
    /// </summary>
    /// <param name="user">当前用户（不可为 null；匿名访客用 Id=0 的 Guest）。</param>
    /// <param name="fullPath">已经过 <see cref="TryResolvePath"/> 的完整路径。</param>
    /// <param name="operation">操作类型。</param>
    /// <param name="password">目录密码（WebDAV / S3 / FTP 没有该通道，传 null）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<AccessDecision> AuthorizeAsync(
        User user, string fullPath, FileOperation operation, string? password, CancellationToken ct = default)
    {
        if (user.Disabled)
            return AccessDecision.Deny(RespCodes.Unauthorized, "user disabled");

        var meta = await NearestMetaAsync(fullPath, ct);
        return Authorize(user, fullPath, operation, password, meta is null ? [] : new[] { meta });
    }

    /// <summary>
    /// 已持有 meta 时的授权判定（批量列举用）。语义与
    /// <see cref="AuthorizeAsync(User,string,FileOperation,string?,CancellationToken)"/> 完全一致。
    /// </summary>
    /// <param name="user">当前用户。</param>
    /// <param name="fullPath">完整虚拟路径。</param>
    /// <param name="operation">操作类型。</param>
    /// <param name="password">目录密码。</param>
    /// <param name="metas">已加载的全量 meta（或仅包含相关项的子集）。</param>
    public AccessDecision Authorize(
        User user, string fullPath, FileOperation operation, string? password, IEnumerable<Meta> metas)
    {
        if (user.Disabled)
            return AccessDecision.Deny(RespCodes.Unauthorized, "user disabled");

        if (operation == FileOperation.Read)
        {
            var meta = MetaAccess.FindNearest(metas, fullPath);
            return MetaAccess.CanAccess(user, meta, fullPath, password)
                ? AccessDecision.Allow
                : AccessDecision.Deny(RespCodes.Forbidden, "password is incorrect or you have no permission");
        }

        // 写操作只由「是否已登录」决定。
        return user.IsAdmin()
            ? AccessDecision.Allow
            : AccessDecision.Deny(RespCodes.Forbidden, "permission denied");
    }

    /// <summary>
    /// 解析 + 授权一步完成。
    /// </summary>
    public async Task<(string FullPath, AccessDecision Decision)> ResolveAndAuthorizeAsync(
        User user, string? requestedPath, FileOperation operation, string? password, CancellationToken ct = default)
    {
        TryResolvePath(user, requestedPath, out var fullPath);
        return (fullPath, await AuthorizeAsync(user, fullPath, operation, password, ct));
    }

    /// <summary>
    /// 目录列举时的单条目可见性：隐藏规则与目录密码两项。
    /// 与 <see cref="AuthorizeAsync"/> 的读取分支完全相同，便于 PROPFIND / LIST 逐项过滤 ——
    /// 只检查父目录会让受保护子目录「列得出来、打不开」，仍然泄露名字。
    /// </summary>
    public async Task<bool> CanSeeAsync(User user, string fullPath, string? password, CancellationToken ct = default)
    {
        if (user.Disabled) return false;
        var decision = await AuthorizeAsync(user, fullPath, FileOperation.Read, password, ct);
        return decision.Allowed;
    }

    /// <summary>已持有 meta 列表时的可见性判定（列目录逐项过滤用）。</summary>
    public bool CanSee(User user, string fullPath, string? password, IEnumerable<Meta> metas)
        => Authorize(user, fullPath, FileOperation.Read, password, metas).Allowed;
}
