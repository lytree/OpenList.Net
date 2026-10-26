using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Vfs;
using DirectUploadInfo = OpenList.Core.Interfaces.DirectUploadInfo;
using ArchiveEntry = OpenList.Core.Interfaces.ArchiveEntry;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 文件系统操作服务，统一经过 <see cref="MountService"/> 的 <see cref="MountFileSystem"/> 路由。
/// 对应 Go 参考实现 <c>internal/fs</c>。操作基于路径；挂载服务负责解析底层驱动。
/// 可选驱动能力（链接/上传/归档）通过 <see cref="OpenList.Core.Drivers"/> 中的扩展接口探测。
/// </summary>
public sealed class FileSystemService : IFileSystemService
{
    private readonly MountService _mount;
    private readonly ISignService? _signs;
    private readonly IOfflineDownloadService? _offline;
    private readonly IObjsUpdateHooks? _hooks;
    private readonly IServiceScopeFactory? _scopes;
    private readonly ITaskService? _tasks;
    private readonly IPublicUrlResolver? _publicUrls;

    /// <summary>创建文件系统服务实例（仅用于单元测试）。</summary>
    public FileSystemService(MountService mount) => _mount = mount;

    /// <summary>
    /// 创建文件系统服务实例（带签名服务，用于在 GetAsync 中生成 raw_url）。
    /// </summary>
    /// <param name="mount">挂载服务。</param>
    /// <param name="signs">签名服务。</param>
    public FileSystemService(MountService mount, ISignService signs)
    {
        _mount = mount;
        _signs = signs;
    }

    /// <summary>
    /// 创建文件系统服务实例（带离线下载编排支持）。
    /// </summary>
    /// <param name="mount">挂载服务。</param>
    /// <param name="signs">签名服务（可选）。</param>
    /// <param name="offline">离线下载服务（可选）。</param>
    public FileSystemService(MountService mount, ISignService? signs, IOfflineDownloadService? offline)
        : this(mount, signs, offline, null, null, null)
    {
    }

    /// <summary>
    /// 创建文件系统服务实例（带离线下载编排与对象更新钩子发布）。
    /// </summary>
    /// <param name="mount">挂载服务。</param>
    /// <param name="signs">签名服务（可选）。</param>
    /// <param name="offline">离线下载服务（可选）。</param>
    /// <param name="hooks">对象更新钩子注册表（可选，用于增量索引等）。</param>
    public FileSystemService(MountService mount, ISignService? signs, IOfflineDownloadService? offline, IObjsUpdateHooks? hooks)
        : this(mount, signs, offline, hooks, null, null)
    {
    }

    /// <summary>
    /// 完整构造：所有可选依赖可显式注入。
    /// </summary>
    /// <param name="mount">挂载服务。</param>
    /// <param name="signs">签名服务（可选）。</param>
    /// <param name="offline">离线下载服务（可选）。</param>
    /// <param name="hooks">对象更新钩子注册表（可选，用于增量索引等）。</param>
    /// <param name="scopes">DI 作用域工厂（用于解析 scoped 的 IMetaService，可选）。</param>
    /// <param name="tasks">后台任务服务（用于 move/copy 任务化，可选）。</param>
    /// <param name="publicUrls">站点基地址解析器（用于把 raw_url 生成为绝对地址，可选）。</param>
    public FileSystemService(
        MountService mount,
        ISignService? signs,
        IOfflineDownloadService? offline,
        IObjsUpdateHooks? hooks,
        IServiceScopeFactory? scopes,
        ITaskService? tasks,
        IPublicUrlResolver? publicUrls = null)
    {
        _mount = mount;
        _signs = signs;
        _offline = offline;
        _hooks = hooks;
        _scopes = scopes;
        _tasks = tasks;
        _publicUrls = publicUrls;
    }

    /// <summary>
    /// 列出目录下的条目，目录优先、名称字典序，并应用分页。
    /// </summary>
    public async Task<Resp<FsListResp>> ListAsync(FsListReq req, User user, CancellationToken ct = default)
    {
        var path = Normalize(req.Path);
        if (!_mount.Root.DirectoryExists(path))
            return Resp<FsListResp>.Fail("path not found", RespCodes.NotFound);

        // Go fsread.go：refresh=true 要求该路径可写；非 admin 路径下报 403。
        // 决定权交给前端："刷新" 按钮仅管理员可见，但接口侧仍守一道。
        if (req.Refresh && !user.IsAdmin())
            return Resp<FsListResp>.Fail("refresh without permission", RespCodes.Forbidden);

        // 签名策略与 Go 的 isEncrypt(meta, reqPath) 一样，每个请求只算一次，条目共用。
        var signCtx = await ResolveSignContextAsync(path.ToString(), ct);
        var entries = _mount.Root.EnumeratePaths(path)
            .Select(p => _mount.Root.FileExists(p)
                ? (FileSystemEntry)_mount.Root.GetFileEntry(p)
                : _mount.Root.GetDirectoryEntry(p))
            .Select(e => ToObj(e, signCtx))
            .OrderBy(e => !e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 发布该目录最新条目列表，让订阅者（增量索引）在此刻与真实文件系统对齐。
        if (_hooks is not null)
            _ = _hooks.PublishDirectoryUpdateAsync(path.ToString(), entries, CancellationToken.None);

        var total = entries.Count;
        // 镜像 Go 的 PageReq.Validate()：page<1 → 1；per_page<1 → int.MaxValue（返回全部）。
        // 前端可能省略 per_page（默认 0），此时应返回全部条目。
        var page = Math.Max(1, req.Page);
        var perPage = req.PerPage < 1 ? int.MaxValue : req.PerPage;
        var paged = entries.Skip((page - 1) * perPage).Take(perPage).ToList();

        var driver = _mount.FindDriverForPath(req.Path);
        // 尝试加载 nearest meta，用于 readme/header 注入与"该路径可写"判定。
        // 单元测试不走 DI 时 _scopes==null，跳过 meta 解析（保持旧行为）。
        Meta? nearest = null;
        if (_scopes is not null)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var metaSvc = scope.ServiceProvider.GetService<IMetaService>();
                if (metaSvc is not null)
                {
                    var metaList = (await metaSvc.ListAsync(ct)).Data ?? [];
                    nearest = FindNearestMeta(metaList, path.ToString());
                }
            }
            catch { /* meta 解析失败不影响 list 本体 */ }
        }

        // 写入条件：驱动非只读 AND（用户可写 OR meta 显式标记 Write=true）。
        // 与 Go fsread.go 的 CanWrite(meta, path) 语义对齐。
        var driverWritable = driver?.ReadOnly == false;
        var metaForcesWrite = nearest is { Write: true };
        var write = (driverWritable && (user.IsAdmin() || user.CanWriteContent())) || metaForcesWrite;

        // 镜像 Go isEncrypt(meta, path)：meta 含密码 + RSub/PSub 覆盖则视为加密（readme/header 取 meta）。
        var readme = nearest is { Readme: { Length: > 0 } r, RSub: true } ? r
            : nearest is { Readme: { Length: > 0 } r2 } && PathCoveredByMeta(path.ToString(), nearest.Path, "/") ? r2
            : null;
        var header = nearest is { Header: { Length: > 0 } h, HeaderSub: true } ? h
            : nearest is { Header: { Length: > 0 } h2 } && PathCoveredByMeta(path.ToString(), nearest.Path, "/") ? h2
            : null;

        return Resp<FsListResp>.Ok(new FsListResp
        {
            Content = paged,
            Total = total,
            Provider = driver?.DriverName,
            DirectUploadTools = driver is IFileSystemDirectUploader ? new List<string> { "direct" } : null,
            Write = write,
            WriteContentBypass = metaForcesWrite && !user.CanWriteContent(),
            Readme = readme,
            Header = header,
        });
    }

    /// <summary>
    /// 从 meta 列表中找到路径前缀最长匹配的那条。Go `op.GetNearestMeta` 语义。
    /// </summary>
    private static Meta? FindNearestMeta(IReadOnlyList<Meta> metas, string path)
    {
        Meta? best = null;
        var bestLen = -1;
        foreach (var m in metas)
        {
            if (string.IsNullOrEmpty(m.Path)) continue;
            if (path == m.Path || path.StartsWith(m.Path.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                if (m.Path.Length > bestLen)
                {
                    best = m;
                    bestLen = m.Path.Length;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// 简化版 "meta 覆盖路径" 判断：路径 == meta.Path 或以 "meta.Path/" 开头。
    /// </summary>
    private static bool PathCoveredByMeta(string path, string metaPath, string rSub)
    {
        var mp = metaPath.TrimEnd('/');
        if (string.IsNullOrEmpty(mp)) return true; // 根 meta 默认覆盖所有
        return path == mp || path.StartsWith(mp + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// 获取文件或目录的详情。对于文件，若配置了签名服务则额外生成指向签名代理端点的 raw_url。
    /// 当目标是视频文件且位于可列举的目录时，附带同目录同名（去除扩展名后）前缀的兄弟条目。
    /// </summary>
    public async Task<Resp<FsGetResp>> GetAsync(FsGetReq req, User user, CancellationToken ct = default)
    {
        var path = Normalize(req.Path);
        var isFile = _mount.Root.FileExists(path);
        var isDir = _mount.Root.DirectoryExists(path);
        if (!isFile && !isDir)
            return Resp<FsGetResp>.Fail("path not found", RespCodes.NotFound);

        FileSystemEntry? entry = isFile
            ? _mount.Root.GetFileEntry(path)
            : isDir ? _mount.Root.GetDirectoryEntry(path) : null;
        var driver = _mount.FindDriverForPath(req.Path);
        var resp = new FsGetResp
        {
            Provider = driver?.DriverName,
        };
        // 签名策略每个请求解析一次，条目共用：对齐 Go 的 isEncrypt(meta, reqPath) 只算一次。
        var signCtx = await ResolveSignContextAsync(path.ToString(), ct);
        if (entry is not null)
        {
            var obj = ToObj(entry, signCtx);
            resp.Name = obj.Name;
            resp.Size = obj.Size;
            resp.IsDir = obj.IsDir;
            resp.Modified = obj.Modified;
            resp.Created = obj.Created;
            resp.Type = obj.Type;
            // 前端把 sign 拼到 /d/ 直链上（外部播放器用），与 raw_url 的签名同源。
            resp.Sign = obj.Sign;
        }
        // 对文件生成指向代理端点 /p 的 raw_url。
        // 这是前端用作视频/音频/pdf 预览的媒体源；路径是否需要签名由 signCtx 决定，
        // 与 /p/ 端的校验共用同一判定（Go 的 needSign）。
        if (isFile && _signs is not null)
        {
            // 必须用规范化后的路径签名：/p/ 端点是拿路由捕获到的路径重算签名来校验的，
            // 直接签 "req.Path" 在调用方省略前导斜杠或带多余斜杠时会得到一张对不上的签名。
            resp.RawUrl = BuildRawUrl(path.ToString(), signCtx);
        }

        // Related：与 Go 9de3f69b 一致 —— 仅当目标是「视频文件」时，才列同目录相似名（去扩展名前缀）的兄弟条目。
        // 这样前端可以直接在视频详情页切换同系列文件。
        if (isFile && resp.Type == TypeVideo)
        {
            var parent = path.GetDirectory();
            var baseName = TrimExtension(resp.Name);
            if (!string.IsNullOrEmpty(baseName) && _mount.Root.DirectoryExists(parent))
            {
                var siblings = new List<Obj>();
                foreach (var p in _mount.Root.EnumeratePaths(parent))
                {
                    if (_mount.Root.FileExists(p))
                    {
                        var fe = _mount.Root.GetFileEntry(p);
                        var sibling = ToObj(fe, signCtx);
                        if (sibling.Name != resp.Name && sibling.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                            siblings.Add(sibling);
                    }
                }
                resp.Related = siblings;
            }
        }
        return Resp<FsGetResp>.Ok(resp);
    }

    /// <summary>剥离文件扩展名，保留剩余主体（用于 related 前缀匹配）。</summary>
    private static string TrimExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    /// <summary>
    /// 构建代理 URL：<c>{站点基地址}/p/{path}</c>，路径段逐个进行 URL 编码，
    /// 以保留 '/' 分隔符（ASP.NET 通配路由所需）。需要签名时追加 <c>?sign=</c>。
    /// <para>
    /// 必须是<b>绝对地址</b>：这份 URL 的主要消费方是浏览器之外的程序（IINA / Vivid /
    /// 下载器），它们不会用页面 origin 去解析相对路径。对齐 Go <c>fsread.go</c> 用
    /// <c>common.GetApiUrl(c)</c> 拼 <c>/p</c> 的做法。站点基地址解析不出来时退回相对地址。
    /// </para>
    /// </summary>
    private string BuildRawUrl(string path, SignContext signCtx)
    {
        // 逐段编码以保留 '/' 作为路径分隔符。
        // 前导 '/' 产生一个空首段，将其去掉以避免双斜杠。
        var segments = path.Split('/').SkipWhile(string.IsNullOrEmpty).Select(Uri.EscapeDataString);
        var encoded = string.Join('/', segments);
        var baseUrl = _publicUrls?.Resolve() ?? string.Empty;

        var url = $"{baseUrl}/p/{encoded}";
        if (signCtx.NeedSign)
            url += "?sign=" + Uri.EscapeDataString(_signs!.Sign(path, signCtx.ExpireAt));
        return url;
    }

    /// <summary>
    /// 解析某路径的签名策略。未注入签名服务（单元测试）时视为「不需要签名」。
    /// </summary>
    private async Task<SignContext> ResolveSignContextAsync(string path, CancellationToken ct)
        => _signs is null ? SignContext.NotNeeded : await _signs.GetContextAsync(path, ct);

    /// <summary>新建目录；若已存在返回冲突。</summary>
    public Task<Resp> MkdirAsync(string path, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        if (_mount.Root.FileExists(upath) || _mount.Root.DirectoryExists(upath))
            return Task.FromResult(Resp.Fail("path already exists", RespCodes.Conflict));
        try
        {
            _mount.Root.CreateDirectory(upath);
            PublishDir(upath.GetDirectory().ToString());
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    /// <summary>重命名/移动文件或目录。</summary>
    public Task<Resp> RenameAsync(string oldPath, string newPath, User user, CancellationToken ct = default)
    {
        var src = Normalize(oldPath);
        var dst = Normalize(newPath);
        if (!_mount.Root.FileExists(src) && !_mount.Root.DirectoryExists(src))
            return Task.FromResult(Resp.Fail("source not found", RespCodes.NotFound));
        try
        {
            var isDir = _mount.Root.DirectoryExists(src);
            if (_mount.Root.FileExists(src))
                _mount.Root.MoveFile(src, dst);
            else
                _mount.Root.MoveDirectory(src, dst);
            // 目录重命名时旧路径整棵子树在索引中过期，先清理再刷新两侧父目录。
            if (isDir) PublishRemoved(src.ToString());
            PublishDir(src.GetDirectory().ToString());
            PublishDir(dst.GetDirectory().ToString());
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    /// <summary>批量重命名；任一项失败即中止并返回该错误。</summary>
    public Task<Resp> BatchRenameAsync(IReadOnlyList<BatchRenameItem> items, User user, CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            var r = RenameAsync(item.SrcName, item.NewName, user, ct).GetAwaiter().GetResult();
            if (r.Code != 200) return Task.FromResult(r);
        }
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 正则批量重命名：在 <paramref name="srcDir"/> 下匹配 <paramref name="srcReg"/> 的文件，
    /// 用 <paramref name="newReg"/> 替换生成新名后依次重命名。
    /// </summary>
    public Task<Resp> RegexRenameAsync(string srcDir, string srcReg, string newReg, User user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(srcReg))
            return Task.FromResult(Resp.Fail("src_name_regex is required", RespCodes.BadRequest));
        Regex regex;
        try { regex = new Regex(srcReg); }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }

        var dir = Normalize(srcDir);
        if (!_mount.Root.DirectoryExists(dir))
            return Task.FromResult(Resp.Fail("src_dir not found", RespCodes.NotFound));

        try
        {
            var items = new List<BatchRenameItem>();
            foreach (var p in _mount.Root.EnumeratePaths(dir))
            {
                var name = p.GetName();
                if (!regex.IsMatch(name)) continue;
                var newName = regex.Replace(name, newReg);
                if (string.IsNullOrEmpty(newName) || newName == name) continue;
                items.Add(new BatchRenameItem
                {
                    SrcName = UPath.Combine(dir, name).FullName,
                    NewName = UPath.Combine(dir, newName).FullName,
                });
            }
            foreach (var item in items)
            {
                var r = RenameAsync(item.SrcName, item.NewName, user, ct).GetAwaiter().GetResult();
                if (r.Code != 200) return Task.FromResult(r);
            }
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    /// <summary>把多个源路径移动到目标目录下。</summary>
    public Task<Resp> MoveAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default)
    {
        var dst = Normalize(destDir);
        if (!_mount.Root.DirectoryExists(dst))
            return Task.FromResult(Resp.Fail("destination not found", RespCodes.NotFound));
        try
        {
            foreach (var src in srcPaths)
            {
                var s = Normalize(src);
                if (!_mount.Root.FileExists(s) && !_mount.Root.DirectoryExists(s)) continue;
                var target = UPath.Combine(dst, s.GetName());
                if (_mount.Root.FileExists(s))
                    _mount.Root.MoveFile(s, target);
                else
                    _mount.Root.MoveDirectory(s, target);
            }
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    /// <summary>递归移动（单源移动的别名）。</summary>
    public Task<Resp> RecursiveMoveAsync(string srcPath, string destDir, User user, CancellationToken ct = default)
        => MoveAsync([srcPath], destDir, user, ct);

    /// <summary>把多个源路径复制到目标目录下（文件与目录均支持递归）。</summary>
    public Task<Resp> CopyAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default)
        => CopyInternalAsync(srcPaths, destDir, merge: false, ct);

    /// <summary>
    /// 任务化批量移动（与 Go FsMove 语义对齐）：
    /// <list type="number">
    /// <item>校验权限与 names 非空；</item>
    /// <item>用 <paramref name="req"/> 解析出每个 src/dst 完整路径；</item>
    /// <item>检查 Overwrite/SkipExisting/Merge；</item>
    /// <item>为每条剩下的路径通过 <see cref="ITaskService"/> 创建一个 Move 任务（fire-and-forget），返回 tid 列表。</item>
    /// </list>
    /// 失败原因以 4xx 返回；没有任何名字可移动时 message 仍为 "Move operations completed immediately"。
    /// </summary>
    public async Task<Resp<FsManageResp>> MoveBatchAsync(MoveCopyReq req, User user, CancellationToken ct = default)
    {
        if (!user.CanMove() && !user.IsAdmin())
            return Resp<FsManageResp>.Fail("permission denied", RespCodes.Forbidden);
        if (req.Names.Count == 0)
            return Resp<FsManageResp>.Fail("empty file names", RespCodes.BadRequest);

        var srcDir = Normalize(req.SrcDir);
        var dstDir = Normalize(req.DstDir);
        if (!_mount.Root.DirectoryExists(srcDir))
            return Resp<FsManageResp>.Fail("src_dir not found", RespCodes.NotFound);
        if (!_mount.Root.DirectoryExists(dstDir))
            return Resp<FsManageResp>.Fail("dst_dir not found", RespCodes.NotFound);

        // 以带尾斜杠的前缀做越界校验：UPath 归一化会去掉尾斜杠，
        // 因此这里显式构造字符串前缀，确保 name 中的 "../" 无法逃逸出 src_dir。
        var srcPrefix = srcDir.FullName.EndsWith("/") ? srcDir.FullName : srcDir.FullName + "/";
        var valid = new List<string>(req.Names.Count);
        for (int i = 0; i < req.Names.Count; i++)
        {
            var name = req.Names[i];
            // 镜像 Go checkRelativePath：names 只允许纯名称，含路径成分（/ \ . ..）一律拒绝，
            // 否则 UPath.Combine 归一化 "../" 后可越出 src_dir。
            if (!UPath.IsValidName(name))
                return Resp<FsManageResp>.Fail("using relative path is not allowed", RespCodes.Forbidden);
            var src = UPath.Combine(srcDir, name);
            if (!src.FullName.StartsWith(srcPrefix, StringComparison.Ordinal))
                continue;
            if (!req.Overwrite)
            {
                var baseN = src.GetName();
                if (baseN == "." || baseN == "/")
                    return Resp<FsManageResp>.Fail($"invalid file name [{name}]", RespCodes.BadRequest);
                var dst = UPath.Combine(dstDir, baseN);
                if (_mount.Root.FileExists(dst) || _mount.Root.DirectoryExists(dst))
                {
                    if (!req.SkipExisting)
                        return Resp<FsManageResp>.Fail($"file [{name}] exists", RespCodes.Forbidden);
                    continue;
                }
            }
            valid.Add(src.FullName);
        }
        if (valid.Count == 0)
            return Resp<FsManageResp>.Ok(new FsManageResp { Message = "Move operations completed immediately", Tasks = [] });

        // 任务化执行：依次把每条 src 移动到 dstDir 下。
        var tids = await Task.WhenAll(valid.Select(src => _tasks!.CreateAsync(TaskType.Move,
            (progress, cancel) => DoMoveAsync(src, dstDir, progress, cancel), ct)));
        return Resp<FsManageResp>.Ok(new FsManageResp
        {
            Message = $"Successfully created {tids.Length} move task(s)",
            Tasks = tids.Select(tid => new TaskInfo { Id = tid, Type = TaskType.Move, Name = tid }).ToList(),
        });
    }

    /// <summary>
    /// 任务化批量复制（与 Go FsCopy 对齐）：
    /// <list type="number">
    /// <item><c>overwrite=false</c> 时：同名文件返回 403；同名目录在 <c>merge=true</c> 时合并，在 <c>merge=false</c> 时返回 403；</item>
    /// <item>通过 <see cref="ITaskService"/> 创建 Copy 任务，返回 tid 列表。</item>
    /// </list>
    /// </summary>
    public async Task<Resp<FsManageResp>> CopyBatchAsync(MoveCopyReq req, User user, CancellationToken ct = default)
    {
        if (!user.CanCopy() && !user.IsAdmin())
            return Resp<FsManageResp>.Fail("permission denied", RespCodes.Forbidden);
        if (req.Names.Count == 0)
            return Resp<FsManageResp>.Fail("empty file names", RespCodes.BadRequest);

        var srcDir = Normalize(req.SrcDir);
        var dstDir = Normalize(req.DstDir);
        if (!_mount.Root.DirectoryExists(srcDir))
            return Resp<FsManageResp>.Fail("src_dir not found", RespCodes.NotFound);
        if (!_mount.Root.DirectoryExists(dstDir))
            return Resp<FsManageResp>.Fail("dst_dir not found", RespCodes.NotFound);

        // 以带尾斜杠的前缀做越界校验：UPath 归一化会去掉尾斜杠，
        // 因此这里显式构造字符串前缀，确保 name 中的 "../" 无法逃逸出 src_dir。
        var srcPrefix = srcDir.FullName.EndsWith("/") ? srcDir.FullName : srcDir.FullName + "/";
        var valid = new List<string>(req.Names.Count);
        for (int i = 0; i < req.Names.Count; i++)
        {
            var name = req.Names[i];
            // 镜像 Go checkRelativePath：names 只允许纯名称，含路径成分（/ \ . ..）一律拒绝，
            // 否则 UPath.Combine 归一化 "../" 后可越出 src_dir。
            if (!UPath.IsValidName(name))
                return Resp<FsManageResp>.Fail("using relative path is not allowed", RespCodes.Forbidden);
            var src = UPath.Combine(srcDir, name);
            if (!src.FullName.StartsWith(srcPrefix, StringComparison.Ordinal))
                continue;
            if (!req.Overwrite)
            {
                var baseN = src.GetName();
                if (baseN == "." || baseN == "/")
                    return Resp<FsManageResp>.Fail($"invalid file name [{name}]", RespCodes.BadRequest);
                var dst = UPath.Combine(dstDir, baseN);
                var existsAsFile = _mount.Root.FileExists(dst);
                var existsAsDir = _mount.Root.DirectoryExists(dst);
                if (existsAsFile || existsAsDir)
                {
                    if (!req.SkipExisting && !req.Merge)
                        return Resp<FsManageResp>.Fail($"file [{name}] exists", RespCodes.Forbidden);
                    if (!req.Merge || !existsAsDir)
                        continue;
                }
            }
            valid.Add(src.FullName);
        }
        if (valid.Count == 0)
            return Resp<FsManageResp>.Ok(new FsManageResp { Message = "Copy operations completed immediately", Tasks = [] });

        var tids = await Task.WhenAll(valid.Select(src => _tasks!.CreateAsync(TaskType.Copy,
            (progress, cancel) => DoCopyAsync(src, dstDir, req.Merge, progress, cancel), ct)));
        return Resp<FsManageResp>.Ok(new FsManageResp
        {
            Message = $"Successfully created {tids.Length} copy task(s)",
            Tasks = tids.Select(tid => new TaskInfo { Id = tid, Type = TaskType.Copy, Name = tid }).ToList(),
        });
    }

    /// <summary>任务体：在同一挂载根内把 src 移到 dstDir 下同名位置，并发布 IObjsUpdateHooks。</summary>
    private async Task DoMoveAsync(string srcPath, UPath dstDir, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        var src = Normalize(srcPath);
        if (!_mount.Root.FileExists(src) && !_mount.Root.DirectoryExists(src))
            throw new FileNotFoundException($"source not found: {srcPath}");
        var target = UPath.Combine(dstDir, src.GetName());
        var isDir = _mount.Root.DirectoryExists(src);
        if (isDir) PublishRemoved(src.ToString());
        if (_mount.Root.FileExists(src))
            _mount.Root.MoveFile(src, target);
        else
            _mount.Root.MoveDirectory(src, target);
        PublishDir(src.GetDirectory().ToString());
        PublishDir(dstDir.ToString());
        progress.Report(new TaskProgress { Current = 1, Total = 1 });
        await Task.CompletedTask;
    }

    /// <summary>任务体：把 src 复制到 dstDir 下同名位置；merge=true 时把同名目录的内容合并到目标目录。</summary>
    private async Task DoCopyAsync(string srcPath, UPath dstDir, bool merge, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        var src = Normalize(srcPath);
        if (!_mount.Root.FileExists(src) && !_mount.Root.DirectoryExists(src))
            throw new FileNotFoundException($"source not found: {srcPath}");
        var target = UPath.Combine(dstDir, src.GetName());
        if (_mount.Root.FileExists(src))
        {
            CopyFile(src, target);
        }
        else if (_mount.Root.DirectoryExists(src))
        {
            if (merge && _mount.Root.DirectoryExists(target))
            {
                // merge：把 src 的内容复制到已存在的 target 下（保持同名唯一语义）。
                foreach (var p in _mount.Root.EnumeratePaths(src))
                {
                    var inner = UPath.Combine(target, p.GetName());
                    if (_mount.Root.FileExists(p)) CopyFile(p, inner);
                    else if (_mount.Root.DirectoryExists(p)) CopyDir(p, inner);
                }
            }
            else
            {
                CopyDir(src, target);
            }
        }
        PublishDir(dstDir.ToString());
        progress.Report(new TaskProgress { Current = 1, Total = 1 });
        await Task.CompletedTask;
    }

    /// <summary>
    /// 同步版复制（保留供单元测试与递归移动使用）。merge=true 时同名目录会被合并到目标目录。
    /// </summary>
    private Task<Resp> CopyInternalAsync(IReadOnlyList<string> srcPaths, string destDir, bool merge, CancellationToken ct = default)
    {
        var dst = Normalize(destDir);
        if (!_mount.Root.DirectoryExists(dst))
            return Task.FromResult(Resp.Fail("destination not found", RespCodes.NotFound));
        try
        {
            foreach (var src in srcPaths)
            {
                var s = Normalize(src);
                if (!_mount.Root.FileExists(s) && !_mount.Root.DirectoryExists(s)) continue;
                var target = UPath.Combine(dst, s.GetName());
                if (_mount.Root.FileExists(s))
                {
                    CopyFile(s, target);
                }
                else if (_mount.Root.DirectoryExists(s))
                {
                    if (merge && _mount.Root.DirectoryExists(target))
                    {
                        foreach (var p in _mount.Root.EnumeratePaths(s))
                        {
                            var inner = UPath.Combine(target, p.GetName());
                            if (_mount.Root.FileExists(p)) CopyFile(p, inner);
                            else if (_mount.Root.DirectoryExists(p)) CopyDir(p, inner);
                        }
                    }
                    else
                    {
                        CopyDir(s, target);
                    }
                }
            }
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    /// <summary>删除多个路径（目录递归删除）。</summary>
    public Task<Resp> RemoveAsync(IReadOnlyList<string> paths, User user, CancellationToken ct = default)
    {
        try
        {
            foreach (var p in paths)
            {
                var upath = Normalize(p);
                if (_mount.Root.FileExists(upath)) _mount.Root.DeleteFile(upath);
                else if (_mount.Root.DirectoryExists(upath)) _mount.Root.DeleteDirectory(upath, true);
            }
            return Task.FromResult(Resp.Ok());
        }
        catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
    }

    /// <summary>删除空目录；目录非空或不存在时返回错误。</summary>
    public Task<Resp> RemoveEmptyDirectoryAsync(string path, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        if (!_mount.Root.DirectoryExists(upath))
            return Task.FromResult(Resp.Fail("not found", RespCodes.NotFound));

        // 对齐 Go `FsRemoveEmptyDirectory`（前端文案：*delete all its empty subfolders*）：
        // 自底向上清掉 path 下的空子目录，**path 本身保留**。
        //
        // 旧实现只判断 path 自身是否为空再删 path，有两个问题：
        // 1. 语义不符 —— 当前目录非空时直接回 `directory not empty`，「整理下载目录」形同虚设；
        // 2. 假成功 —— path 是挂载点根时 `DeleteDirectory` 在 MountFileSystem 上不生效，
        //    却仍然返回 200，用户以为清理过了。
        RemoveEmptySubdirectories(upath, 0);
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 自底向上删除空子目录，返回删除数量。
    ///
    /// 单个目录删除失败（只读挂载、驱动拒绝、占用）时**跳过而不是抛**：
    /// 清理动作本身就是尽力而为，删到一半报错会让用户不知道哪部分生效了。
    /// </summary>
    private int RemoveEmptySubdirectories(UPath dir, int depth)
    {
        // 防御性上限：某些驱动的 EnumeratePaths 可能返回包含自身的路径，避免无限递归。
        if (depth > 64) return 0;

        List<UPath> children;
        try
        {
            children = _mount.Root.EnumeratePaths(dir).ToList();
        }
        catch
        {
            return 0;
        }

        var removed = 0;
        foreach (var child in children)
        {
            bool isDir;
            try { isDir = _mount.Root.DirectoryExists(child); }
            catch { continue; }
            if (!isDir) continue;

            removed += RemoveEmptySubdirectories(child, depth + 1);
            try
            {
                if (!_mount.Root.EnumeratePaths(child).Any())
                {
                    _mount.Root.DeleteDirectory(child, isRecursive: false);
                    removed++;
                }
            }
            catch
            {
                // 保留：驱动不允许删除时，空目录留在原地比中断整轮清理更好。
            }
        }
        return removed;
    }

    /// <summary>
    /// 直接写入流内容到指定路径。若底层驱动实现了上传接口则走驱动，否则在根文件系统上写普通文件。
    /// </summary>
    public async Task<Resp> PutStreamAsync(string path, Stream stream, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        var driver = _mount.FindDriverForPath(path);
        try
        {
            if (driver is IFileSystemUploader uploader)
            {
                await uploader.PutAsync(path, stream, ct);
            }
            else
            {
                var parent = upath.GetDirectory();
                if (parent != UPath.Root && !_mount.Root.DirectoryExists(parent))
                    _mount.Root.CreateDirectory(parent);
                using var fs = _mount.Root.OpenFile(upath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(fs, ct);
            }
            return Resp.Ok();
        }
        catch (Exception ex) { return Resp.Fail(ex.Message, RespCodes.InternalError); }
    }

    /// <inheritdoc />
    public Task<Resp<byte[]>> ReadFileAsync(string path, long maxBytes, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        try
        {
            if (!_mount.Root.FileExists(upath))
                return Task.FromResult(Resp<byte[]>.Fail("file not found", RespCodes.NotFound));
            var entry = _mount.Root.GetFileEntry(upath);
            if (entry.Length > maxBytes)
                return Task.FromResult(Resp<byte[]>.Fail($"file exceeds {maxBytes} bytes", RespCodes.BadRequest));

            using var stream = _mount.Root.OpenFile(upath, FileMode.Open, FileAccess.Read);
            using var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            stream.CopyTo(ms, 81920);
            return Task.FromResult(Resp<byte[]>.Ok(ms.ToArray()));
        }
        catch (Exception ex) { return Task.FromResult(Resp<byte[]>.Fail(ex.Message, RespCodes.InternalError)); }
    }

    /// <summary>
    /// 表单上传：按 <paramref name="path"/> 定位驱动（若有上传接口则走驱动），否则把流写入
    /// <paramref name="path"/> 目录下的 <paramref name="fileName"/>。
    /// </summary>
    public Task<Resp> PutFormAsync(string path, Stream stream, string fileName, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemUploader uploader)
        {
            try { uploader.PutFormAsync(path, stream, fileName, ct).GetAwaiter().GetResult(); }
            catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
            return Task.FromResult(Resp.Ok());
        }
        var target = UPath.Combine(upath.GetDirectory(), fileName);
        return PutStreamAsync(target.FullName, stream, user, ct);
    }

    /// <summary>
    /// 获取直传上传信息（适用于支持服务端直传的驱动，如对象存储预签名）。
    /// </summary>
    public Task<Resp<DirectUploadInfo>> GetDirectUploadInfoAsync(string path, long size, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemDirectUploader direct)
        {
            try
            {
                var info = direct.GetDirectUploadInfoAsync(path, size, ct).GetAwaiter().GetResult();
                return Task.FromResult(Resp<DirectUploadInfo>.Ok(new DirectUploadInfo
                {
                    Url = info.Url,
                    Headers = info.Headers,
                    Method = info.Method,
                }));
            }
            catch (Exception ex) { return Task.FromResult(Resp<DirectUploadInfo>.Fail(ex.Message, RespCodes.InternalError)); }
        }
        return Task.FromResult(Resp<DirectUploadInfo>.Fail("driver does not support direct upload", RespCodes.NotImplemented));
    }

    /// <summary>生成路径的分享链接（仅当驱动的链接提供接口存在时）。</summary>
    public Task<Resp<LinkResp>> LinkAsync(string path, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemLinkProvider linkProvider)
        {
            try
            {
                var uri = linkProvider.GetLinkAsync(path, ct).GetAwaiter().GetResult();
                if (uri is not null)
                    return Task.FromResult(Resp<LinkResp>.Ok(new LinkResp { Url = uri.ToString() }));
            }
            catch { }
        }
        return Task.FromResult(Resp<LinkResp>.Fail("link not supported", RespCodes.NotImplemented));
    }

    /// <summary>把请求转交给离线下载服务，未配置时返回 501。</summary>
    public async Task<Resp> AddOfflineDownloadAsync(OfflineDownloadReq req, User user, CancellationToken ct = default)
    {
        if (_offline is null)
            return Resp.Fail("offline download not configured", RespCodes.NotImplemented);
        var r = await _offline.AddAsync(req, user, ct);
        return r.Code == 200 ? Resp.Ok() : Resp.Fail(r.Message, r.Code);
    }

    /// <summary>返回已支持的归档文件扩展名列表。</summary>
    public Task<Resp<ArchiveMetaResp>> ArchiveMetaAsync(string path, User user, CancellationToken ct = default)
        => Task.FromResult(Resp<ArchiveMetaResp>.Ok(new ArchiveMetaResp
        {
            SupportedExtensions = [".zip", ".tar", ".gz", ".rar", ".7z"],
        }));

    /// <summary>列举归档文件内部条目（要求驱动实现归档读取接口）。</summary>
    public Task<Resp<ArchiveListResp>> ArchiveListAsync(string path, string? password, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(path);
        if (driver is IFileSystemArchiveReader reader)
        {
            try
            {
                // 驱动的 System 以其挂载点为根，因此传入相对挂载点的路径。
                var relPath = _mount.ToDriverRelativePath(path);
                var entries = reader.ListArchiveEntriesAsync(relPath, ct).GetAwaiter().GetResult();
                var list = entries.Select(e => new ArchiveEntry
                {
                    Name = e.Name, Size = e.Size, IsDir = e.IsDir, Modified = e.Modified,
                }).ToList();
                return Task.FromResult(Resp<ArchiveListResp>.Ok(new ArchiveListResp { Content = list, Total = list.Count }));
            }
            catch (Exception ex) { return Task.FromResult(Resp<ArchiveListResp>.Fail(ex.Message, RespCodes.InternalError)); }
        }
        return Task.FromResult(Resp<ArchiveListResp>.Fail("driver does not support archive reading", RespCodes.NotImplemented));
    }

    /// <summary>打开归档内的单个条目做内联预览（要求驱动实现归档读取接口）。</summary>
    public Task<Resp<Stream>> OpenArchiveEntryAsync(string archivePath, string entryPath, string? password, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(archivePath);
        if (driver is IFileSystemArchiveReader reader)
        {
            try
            {
                var relArchive = _mount.ToDriverRelativePath(archivePath);
                var stream = reader.OpenArchiveEntryAsync(relArchive, entryPath, password, ct).GetAwaiter().GetResult();
                return Task.FromResult(stream is null
                    ? Resp<Stream>.Fail("entry not found in archive", RespCodes.NotFound)
                    : Resp<Stream>.Ok(stream));
            }
            catch (Exception ex) { return Task.FromResult(Resp<Stream>.Fail(ex.Message, RespCodes.InternalError)); }
        }
        return Task.FromResult(Resp<Stream>.Fail("driver does not support archive reading", RespCodes.NotImplemented));
    }

    /// <summary>把归档文件解压到目标目录（要求驱动实现归档解压接口）。</summary>
    public Task<Resp> ArchiveDecompressAsync(string archivePath, string destPath, string? password, User user, CancellationToken ct = default)
    {
        var driver = _mount.FindDriverForPath(archivePath);
        if (driver is IFileSystemArchiveDecompress decompressor)
        {
            try
            {
                // 驱动的 System 以其挂载点为根，因此传入相对挂载点的路径。
                var relArchive = _mount.ToDriverRelativePath(archivePath);
                var relDest = _mount.ToDriverRelativePath(destPath);
                decompressor.DecompressAsync(relArchive, relDest, password, ct).GetAwaiter().GetResult();
            }
            catch (Exception ex) { return Task.FromResult(Resp.Fail(ex.Message, RespCodes.InternalError)); }
            return Task.FromResult(Resp.Ok());
        }
        return Task.FromResult(Resp.Fail("driver does not support decompression", RespCodes.NotImplemented));
    }

    /// <summary>递归收集目录（最深 3 层），用于前端目录选择器。</summary>
    public Task<Resp<DirsResp>> DirsAsync(string path, User user, CancellationToken ct = default)
    {
        var upath = Normalize(path);
        if (!_mount.Root.DirectoryExists(upath))
            return Task.FromResult(Resp<DirsResp>.Fail("path not found", RespCodes.NotFound));
        var dirs = new List<string>();
        CollectDirs(upath, dirs, maxDepth: 3);
        return Task.FromResult(Resp<DirsResp>.Ok(new DirsResp { Dirs = dirs }));
    }

    /// <summary>杂项文件系统动作（当前未实现）。</summary>
    public Task<Resp> OtherAsync(string path, string action, User user, CancellationToken ct = default)
        => Task.FromResult(Resp.Fail($"action '{action}' not implemented", RespCodes.NotImplemented));

    /// <summary>按关键字递归（或仅当前层）搜索文件系统，返回分页结果。</summary>
    public Task<Resp<SearchResp>> SearchAsync(SearchReq req, User user, CancellationToken ct = default)
    {
        var parent = Normalize(req.Parent);
        if (!_mount.Root.DirectoryExists(parent))
            return Task.FromResult(Resp<SearchResp>.Fail("parent not found", RespCodes.NotFound));
        var results = new List<Obj>();
        SearchRecursive(parent, req.Keywords, req.Recursive, results);
        var total = results.Count;
        // 镜像 Go 的 PageReq.Validate()：page<1 → 1；per_page<1 → int.MaxValue（返回全部）。
        var page = Math.Max(1, req.Page);
        var perPage = req.PerPage < 1 ? int.MaxValue : req.PerPage;
        var paged = results.Skip((page - 1) * perPage).Take(perPage).ToList();
        var nodes = paged.ConvertAll(o => new SearchNode
        {
            Parent = UPath.Combine(parent, "").ToString(),
            Name = o.Name,
            IsDir = o.IsDir,
            Size = o.Size,
            Path = UPath.Combine(parent, o.Name).ToString(),
            Type = o.Type,
        });
        return Task.FromResult(Resp<SearchResp>.Ok(new SearchResp { Content = nodes, Total = total }));
    }

    // --- 辅助方法 ---

    /// <summary>把路径规整为 UPath（补上前导 /）。</summary>
    private static UPath Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return UPath.Root;
        if (!path.StartsWith('/')) path = "/" + path;
        return new UPath(path);
    }

    /// <summary>
    /// 把 文件系统条目转为对外对象（区分文件/目录并推断类型）。
    /// <para>
    /// 仅在 <paramref name="signCtx"/> 要求签名时给<b>文件</b>填 <see cref="Obj.Sign"/>：
    /// 前端（<c>hooks/useLink.ts</c>）靠它给 <c>/d/{path}</c> 直链补上 <c>&amp;sign=</c>；
    /// 为空则不拼 query，直链就靠 <c>/d/</c> 端点的「无需签名」放行（Go 默认行为）。
    /// 目录不签（没有可下载的内容）。内部调用方（索引 / 搜索）不传上下文，签名恒为空。
    /// </para>
    /// </summary>
    private Obj ToObj(FileSystemEntry entry, SignContext? signCtx = null)
    {
        var isDir = entry is DirectoryEntry;
        var needsSign = !isDir && signCtx is { NeedSign: true } && _signs is not null;
        return new Obj
        {
            Name = entry.Name,
            IsDir = isDir,
            Size = isDir ? 0 : (entry as FileEntry)?.Length ?? 0,
            Modified = entry.LastWriteTime,
            Created = entry.CreationTime,
            Type = GetObjType(entry.Name, isDir),
            Sign = needsSign ? _signs!.Sign(entry.Path.ToString(), signCtx!.Value.ExpireAt) : null,
        };
    }

    // 镜像 Go 的 conf const iota：UNKNOWN=0, FOLDER=1, VIDEO=2, AUDIO=3, TEXT=4, IMAGE=5。
    // 前端依据 obj.type 选择预览组件（视频播放器、图片查看器等）。
    private const int TypeUnknown = 0;
    private const int TypeFolder = 1;
    private const int TypeVideo = 2;
    private const int TypeAudio = 3;
    private const int TypeText = 4;
    private const int TypeImage = 5;

    // 取自 Go 的 internal/bootstrap/data/setting.go 的默认扩展名列表。
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "flac", "ogg", "m4a", "wav", "opus", "wma" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "avi", "mov", "rmvb", "webm", "flv", "m3u8" };
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "tiff", "jpeg", "png", "gif", "bmp", "svg", "ico", "swf", "webp", "avif" };
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "htm", "html", "xml", "java", "properties", "sql", "js", "md", "json", "conf", "ini", "vue", "php", "py", "bat", "gitignore", "yml", "go", "sh", "c", "cpp", "h", "hpp", "tsx", "vtt", "srt", "ass", "rs", "lrc", "strm" };

    /// <summary>按文件扩展名推断文件类型。</summary>
    private static int GetFileType(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return TypeUnknown;
        var ext = name[(dot + 1)..];
        if (AudioExts.Contains(ext)) return TypeAudio;
        if (VideoExts.Contains(ext)) return TypeVideo;
        if (ImageExts.Contains(ext)) return TypeImage;
        if (TextExts.Contains(ext)) return TypeText;
        return TypeUnknown;
    }

    /// <summary>目录恒为 TypeFolder，文件类型按扩展名推断。</summary>
    private static int GetObjType(string name, bool isDir)
        => isDir ? TypeFolder : GetFileType(name);

    // --- 对象更新钩子（增量索引等） ---

    /// <summary>
    /// 发布某目录的最新条目列表到 <see cref="IObjsUpdateHooks"/>（fire-and-forget，不阻塞请求）。
    /// 目录已不存在时发布空列表，让订阅者清理该父目录下的过期索引。
    /// </summary>
    private void PublishDir(string path)
    {
        if (_hooks is null) return;
        try
        {
            var upath = Normalize(path);
            if (!_mount.Root.DirectoryExists(upath))
            {
                _ = _hooks.PublishDirectoryUpdateAsync(upath.ToString(), Array.Empty<Obj>(), CancellationToken.None);
                return;
            }
            var entries = _mount.Root.EnumeratePaths(upath)
                .Select(p => _mount.Root.FileExists(p)
                    ? (FileSystemEntry)_mount.Root.GetFileEntry(p)
                    : _mount.Root.GetDirectoryEntry(p))
                // 索引/钩子场景不带签名上下文：签名属于「对外响应」，不属于内部对象。
                .Select(e => ToObj(e, SignContext.NotNeeded))
                .ToList();
            _ = _hooks.PublishDirectoryUpdateAsync(upath.ToString(), entries, CancellationToken.None);
        }
        catch { /* fire-and-forget：钩子失败不影响请求 */ }
    }

    /// <summary>发布路径被删除（含子树）事件到 <see cref="IObjsUpdateHooks"/>。</summary>
    private void PublishRemoved(string path)
    {
        if (_hooks is null) return;
        _ = _hooks.PublishPathRemovedAsync(Normalize(path).ToString(), CancellationToken.None);
    }

    /// <summary>递归收集目录（受最大深度限制）。</summary>
    private void CollectDirs(UPath path, List<string> dirs, int maxDepth)
    {
        if (maxDepth <= 0) return;
        foreach (var p in _mount.Root.EnumeratePaths(path))
        {
            if (!_mount.Root.DirectoryExists(p)) continue;
            dirs.Add(p.FullName);
            CollectDirs(p, dirs, maxDepth - 1);
        }
    }

    /// <summary>递归（或仅当前层）按关键字匹配名称并收集命中对象。</summary>
    private void SearchRecursive(UPath path, string keywords, bool recursive, List<Obj> results)
    {
        foreach (var p in _mount.Root.EnumeratePaths(path))
        {
            FileSystemEntry entry = _mount.Root.FileExists(p)
                ? _mount.Root.GetFileEntry(p)
                : _mount.Root.GetDirectoryEntry(p);
            if (entry.Name.Contains(keywords, StringComparison.OrdinalIgnoreCase))
                results.Add(ToObj(entry));
            if (entry is DirectoryEntry && recursive)
                SearchRecursive(p, keywords, recursive, results);
        }
    }

    /// <summary>复制单个文件内容。</summary>
    private void CopyFile(UPath src, UPath dst)
    {
        using var sIn = _mount.Root.OpenFile(src, FileMode.Open, FileAccess.Read);
        using var sOut = _mount.Root.OpenFile(dst, FileMode.Create, FileAccess.Write);
        sIn.CopyTo(sOut);
    }

    /// <summary>递归复制目录。</summary>
    private void CopyDir(UPath src, UPath dst)
    {
        _mount.Root.CreateDirectory(dst);
        foreach (var entry in _mount.Root.EnumeratePaths(src))
        {
            var target = UPath.Combine(dst, entry.GetName());
            if (_mount.Root.FileExists(entry)) CopyFile(entry, target);
            else if (_mount.Root.DirectoryExists(entry)) CopyDir(entry, target);
        }
    }
}
