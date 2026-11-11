using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.OfflineDownload;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 离线下载编排服务，通过 aria2 / qBittorrent / Transmission / SimpleHttp 实现离线下载。
/// 对应 Go 参考实现 <c>pkg/task/offline_download</c>。
/// <para>
/// **完整状态机是两段任务**（与 Go 一致）：
/// <list type="number">
/// <item>下载段（<c>XxxDown</c>）：把 URL 交给工具，下载到**本机临时目录**；
/// 工具都是独立进程，只能往自己那台机器的磁盘写，因此不能直接给 VFS 路径。</item>
/// <item>转移段（<c>XxxTransfer</c>）：下载完成后另建任务，把临时目录里的产物经
/// VFS 上传进目标存储，并按 <see cref="OfflineDownloadReq.DeletePolicy"/> 清理临时文件。</item>
/// </list>
/// 旧实现只做了第一段（而且把 VFS 路径直接当工具的 <c>dir</c> 传过去），因此
/// "任务显示成功、目标目录里却没有文件"。SimpleHttp 的
/// <c>UploadDownloadStream</c> 策略是唯一例外：边下边传，一个任务走完，不落临时文件。
/// </para>
/// </summary>
public sealed class OfflineDownloadService : IOfflineDownloadService
{
    /// <summary>删除策略：上传成功后删除临时文件（Go <c>DeleteOnUploadSucceed</c>）。</summary>
    public const int DeleteOnUploadSucceed = 0;
    /// <summary>删除策略：上传失败后删除临时文件（Go <c>DeleteOnUploadFailed</c>）。</summary>
    public const int DeleteOnUploadFailed = 1;
    /// <summary>删除策略：从不删除临时文件（Go <c>DeleteNever</c>）。</summary>
    public const int DeleteNever = 2;
    /// <summary>删除策略：无论成败都删除临时文件（Go <c>DeleteAlways</c>）。</summary>
    public const int DeleteAlways = 3;
    /// <summary>删除策略：边下边传，不落临时文件（Go <c>UploadDownloadStream</c>，仅 SimpleHttp）。</summary>
    public const int UploadDownloadStream = 4;

    /// <summary>轮询间隔：各工具的进度接口都不是高频接口，2 秒足够且不会打爆 RPC。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    /// <summary>状态轮询的总时长上限（工具无响应时不至于让任务永久挂起）。</summary>
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(30);

    private readonly ITaskService _tasks;
    private readonly Func<IFileSystemService> _fsFactory;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;

    /// <summary>
    /// 创建离线下载编排服务实例。
    /// </summary>
    /// <param name="tasks">任务服务，用于为每个下载/转移创建一个跟踪任务。</param>
    /// <param name="fsFactory">文件系统服务工厂，用于校验目标目录与执行 transfer 上传。</param>
    /// <param name="scopes">作用域工厂，用于创建数据库作用域以读取工具配置。</param>
    /// <param name="http">HTTP 客户端工厂，用于创建各下载工具所需的客户端。</param>
    public OfflineDownloadService(
        ITaskService tasks,
        Func<IFileSystemService> fsFactory,
        IServiceScopeFactory scopes,
        IHttpClientFactory http)
    {
        _tasks = tasks;
        _fsFactory = fsFactory;
        _scopes = scopes;
        _http = http;
    }

    /// <summary>
    /// 为若干 URL 提交离线下载。按 <paramref name="req"/> 指定的工具与目标目录，
    /// 为每个合法 URL 创建一个后台下载任务（下载完成后自动接一个转移任务）并返回任务 id 列表。
    /// </summary>
    /// <param name="req">离线下载请求，包含 URL 列表、目标目录、工具名与删除策略。</param>
    /// <param name="user">发起下载的用户。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp<OfflineDownloadResp>> AddAsync(OfflineDownloadReq req, User user, CancellationToken ct = default)
    {
        if (req.Urls is null || req.Urls.Count == 0)
            return Resp<OfflineDownloadResp>.Fail("urls is required", RespCodes.BadRequest);
        if (string.IsNullOrWhiteSpace(req.Path))
            return Resp<OfflineDownloadResp>.Fail("path is required", RespCodes.BadRequest);

        // 权限位 2：可添加离线下载。端点层已查一次，这里再查一次是因为调用方不止 HTTP 一条
        // （驱动/插件可能直接调服务），而「能提交任务」等于「能让服务器往目标目录写文件」。
        if (!user.CanAddOfflineDownload())
            return Resp<OfflineDownloadResp>.Fail("permission denied", RespCodes.Forbidden);

        var toolName = (req.Tool ?? "").Trim();
        // 工具名大小写不敏感：GetOfflineDownloadToolsAsync 返回的是展示名（"qBittorrent"）。
        var (downType, transferType, factory) = toolName.ToLowerInvariant() switch
        {
            "aria2" => (TaskType.Aria2Down, TaskType.Aria2Transfer, (Func<CancellationToken, Task<IOfflineDownloadTool>>)BuildAria2Async),
            "qbit" or "qbittorrent" => (TaskType.QbitDown, TaskType.QbitTransfer, BuildQbitAsync),
            "transmission" => (TaskType.TransmissionDown, TaskType.TransmissionTransfer, BuildTransmissionAsync),
            // 空工具名回退 SimpleHttp：它无需任何配置，是唯一"一定可用"的工具。
            "" or "simplehttp" or "simple_http" or "simple" => (TaskType.OfflineDownload, TaskType.OfflineDownloadTransfer, BuildSimpleHttpAsync),
            _ => ((TaskType)(-1), (TaskType)(-1), (Func<CancellationToken, Task<IOfflineDownloadTool>>)null!),
        };
        if (factory is null)
            return Resp<OfflineDownloadResp>.Fail($"unknown offline download tool: {req.Tool}", RespCodes.BadRequest);

        // SimpleHttp 是唯一支持「边下边传」的工具（它在本进程内读流，可直接喂给 VFS）。
        var isSimpleHttp = toolName.ToLowerInvariant() is "" or "simplehttp" or "simple_http" or "simple";

        // 校验目标目录可写，避免在提交任何 URL 前因路径非法而失败。
        var listResp = await _fsFactory().ListAsync(new FsListReq { Path = req.Path, PerPage = 1 }, user, ct);
        if (listResp.Code != 200)
            return Resp<OfflineDownloadResp>.Fail("destination path not found", RespCodes.NotFound);

        var tids = new List<string>();
        foreach (var url in req.Urls)
        {
            var u = url?.Trim();
            if (string.IsNullOrEmpty(u)) continue;

            var capturedUrl = u;
            var capturedFactory = factory;
            var capturedDest = req.Path;
            var capturedPolicy = req.DeletePolicy;
            var capturedUser = user;
            var isSimple = isSimpleHttp;

            var tid = await _tasks.CreateAsync(downType, async (progress, cancel) =>
            {
                if (isSimple && capturedPolicy == UploadDownloadStream)
                {
                    // 唯一不落临时文件的分支：边下边传，一个任务走到底。
                    await RunStreamingAsync(capturedUrl, capturedDest, capturedUser, progress, cancel);
                    return;
                }

                // 每次任务用唯一的本机临时目录：qBittorrent 靠 save_path 反查自己的 hash，
                // 目录必须唯一；同时也保证多家工具的任务互不串文件。
                var workDir = Path.Combine(await ResolveTempRootAsync(), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDir);

                IReadOnlyList<string> sources;
                try
                {
                    sources = await RunDownloadAsync(capturedFactory, capturedUrl, workDir, progress, cancel);
                }
                catch
                {
                    // 下载段失败：按策略清理（默认策略下临时目录里的半成品要留着排查，
                    // 但 DeleteAlways 要求无论成败都清）。
                    ApplyDeletePolicy(capturedPolicy, uploadSucceeded: false, workDir);
                    throw;
                }

                // 下载段成功 → 另建转移任务（对齐 Go：下载与转移是两条独立任务，
                // 前端也按 XxxDown / XxxTransfer 分类展示进度）。
                await _tasks.CreateAsync(transferType, async (tp, tc) =>
                {
                    await RunTransferAsync(workDir, sources, capturedDest, capturedPolicy, capturedUser, tp, tc);
                }, CancellationToken.None, capturedUser.Id);
            }, ct, user.Id);
            tids.Add(tid);
        }

        return Resp<OfflineDownloadResp>.Ok(new OfflineDownloadResp { Tasks = tids });
    }

    /// <summary>临时目录根：优先设置项 <c>offline_download_temp_dir</c>，否则用系统临时目录。</summary>
    private async Task<string> ResolveTempRootAsync()
    {
        var fromSetting = await GetSettingAsync("offline_download_temp_dir", "");
        var root = string.IsNullOrWhiteSpace(fromSetting)
            ? Path.Combine(Path.GetTempPath(), "openlist-offline")
            : fromSetting!;
        try { Directory.CreateDirectory(root); }
        catch { /* 创建失败时仍返回路径，后续 CreateDirectory(workDir) 会给出明确错误 */ }
        return root;
    }

    /// <summary>
    /// 下载段：构建工具 → 提交到本机临时目录 → 轮询至完成，返回产物路径。
    /// 工具没给产物列表时回退为整个临时目录（目录由我们指定，内容一定是它的产物）。
    /// </summary>
    private async Task<IReadOnlyList<string>> RunDownloadAsync(
        Func<CancellationToken, Task<IOfflineDownloadTool>> factory,
        string url, string workDir,
        IProgress<TaskProgress> progress, CancellationToken cancel)
    {
        var tool = await factory(cancel);
        if (!tool.Enabled)
            throw new InvalidOperationException($"offline download tool '{tool.Name}' is not configured");

        var addResp = await tool.AddDownloadAsync(url, workDir, cancel);
        if (addResp.Code != 200 || string.IsNullOrEmpty(addResp.Data))
            throw new InvalidOperationException(addResp.Message);

        var st = await PollUntilDoneAsync(tool, addResp.Data, progress, cancel);
        if (st.Files.Count == 0)
        {
            // 兜底：工具（尤其 qbit 反查失败退化成合成 id 时）可能给不出产物路径。
            if (!Directory.EnumerateFileSystemEntries(workDir).Any())
                throw new InvalidOperationException("tool reported complete but produced no files");
            return new[] { workDir };
        }
        return st.Files;
    }

    /// <summary>
    /// 轮询下载状态直至终态，返回最后一次状态（含产物路径）。
    /// 取消时**先通知工具**再抛取消：否则 aria2/qbit 那边会继续下载，白白占带宽。
    /// </summary>
    private static async Task<OfflineDownloadStatus> PollUntilDoneAsync(
        IOfflineDownloadTool tool, string externalId,
        IProgress<TaskProgress> progress, CancellationToken cancel)
    {
        using var period = new PeriodicTimer(PollInterval);
        var sw = Stopwatch.StartNew();
        try
        {
            while (await period.WaitForNextTickAsync(cancel))
            {
                var stResp = await tool.GetStatusAsync(externalId, cancel);
                if (stResp.Code != 200 || stResp.Data is null)
                {
                    // 状态查询暂时失败：继续轮询，但超过上限则视为超时。
                    if (sw.Elapsed > PollTimeout)
                        throw new TimeoutException("offline download status polling timed out");
                    continue;
                }

                var st = stResp.Data;
                progress.Report(new TaskProgress { Current = st.Downloaded, Total = st.Total });

                switch (st.State)
                {
                    case "complete":
                        return st;
                    case "error":
                        throw new InvalidOperationException(st.Error ?? "offline download failed");
                    case "canceled":
                    case "unknown":
                        // unknown = 工具侧已找不到该任务（如 qbit 反查 hash 失败退化成了
                        // 合成 id）；canceled = 任务在工具侧被删。两种都等不到 complete，
                        // 继续轮询只会白耗到 30 分钟超时，直接失败并给出可诊断原因。
                        throw new InvalidOperationException(
                            $"offline download {st.State} (external task {externalId})");
                }

                if (sw.Elapsed > PollTimeout)
                    throw new TimeoutException("offline download status polling timed out");
            }
        }
        catch (OperationCanceledException)
        {
            // 通知工具停止（尽力而为：通知失败不应掩盖取消）。
            try { await tool.CancelAsync(externalId, CancellationToken.None); }
            catch { /* 工具不可达时忽略 */ }
            throw;
        }
        throw new OperationCanceledException(cancel);
    }

    /// <summary>
    /// 转移段：把临时目录里的产物经 VFS 上传到目标目录（保留相对路径结构），
    /// 最后按删除策略清理临时文件。任何一个文件失败都让任务失败并保留其余已上传结果。
    /// </summary>
    private async Task RunTransferAsync(
        string workDir, IReadOnlyList<string> sources, string destPath,
        int policy, User user, IProgress<TaskProgress> progress, CancellationToken cancel)
    {
        var fs = _fsFactory();
        var files = new List<(string Local, string Relative)>();
        foreach (var src in sources) CollectFiles(src, workDir, files);

        // 一个产物都没展开出来（目录被工具清空/路径越界被拦）——直接失败，
        // 否则会"成功搬运 0 个文件"，用户在目标目录看到空结果还以为完成了。
        if (files.Count == 0)
            throw new InvalidOperationException("no downloadable files found in the tool output directory");

        var failed = new List<string>();
        var done = 0;
        progress.Report(new TaskProgress { Current = 0, Total = files.Count });

        foreach (var (local, relative) in files)
        {
            cancel.ThrowIfCancellationRequested();
            var dest = CombineVfsPath(destPath, relative);
            try
            {
                // 目标子目录可能不存在（BT 多文件任务会带目录层级），先建父目录。
                var parent = ParentOf(dest);
                if (parent != "/" && parent.Length > 0)
                {
                    var mk = await fs.MkdirAsync(parent, user, cancel);
                    // 已存在不算失败（并发/重试场景常见）。
                    if (mk.Code != 200 && !IsAlreadyExists(mk.Message))
                        throw new InvalidOperationException(mk.Message);
                }

                await using var stream = File.OpenRead(local);
                var put = await fs.PutStreamAsync(dest, stream, user, cancel);
                if (put.Code != 200) throw new InvalidOperationException(put.Message);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add($"{relative}: {ex.Message}");
            }
            progress.Report(new TaskProgress { Current = done, Total = files.Count });
        }

        ApplyDeletePolicy(policy, uploadSucceeded: failed.Count == 0, workDir);

        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"{failed.Count}/{files.Count} file(s) failed to transfer: " + string.Join("; ", failed.Take(5)));
    }

    /// <summary>
    /// SimpleHttp 的「边下边传」：直接 GET 后把响应流交给 VFS 上传，全程不落临时文件。
    /// </summary>
    private async Task RunStreamingAsync(
        string url, string destPath, User user,
        IProgress<TaskProgress> progress, CancellationToken cancel)
    {
        var client = _http.CreateClient("offline_download_simple_http");
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"http {(int)resp.StatusCode} while downloading {url}");

        var total = resp.Content.Headers.ContentLength ?? 0;
        var name = ResolveRemoteFileName(resp, url);
        var dest = CombineVfsPath(destPath, name);

        var fs = _fsFactory();
        var parent = ParentOf(dest);
        if (parent != "/" && parent.Length > 0)
        {
            var mk = await fs.MkdirAsync(parent, user, cancel);
            if (mk.Code != 200 && !IsAlreadyExists(mk.Message))
                throw new InvalidOperationException(mk.Message);
        }

        await using var src = await resp.Content.ReadAsStreamAsync(cancel);
        // 用计数流把读进度透出给任务面板（PutStreamAsync 本身不回报进度）。
        await using var counting = new CountingStream(src, n =>
            progress.Report(new TaskProgress { Current = n, Total = total }));
        var put = await fs.PutStreamAsync(dest, counting, user, cancel);
        if (put.Code != 200) throw new InvalidOperationException(put.Message);
        progress.Report(new TaskProgress { Current = total > 0 ? total : counting.Total, Total = total });
    }

    /// <summary>按删除策略清理临时目录（<see cref="DeleteNever"/> 保留文件用于排查）。</summary>
    private static void ApplyDeletePolicy(int policy, bool uploadSucceeded, string workDir)
    {
        var shouldDelete = policy switch
        {
            DeleteOnUploadSucceed => uploadSucceeded,
            DeleteOnUploadFailed => !uploadSucceeded,
            DeleteAlways => true,
            // DeleteNever：不动。UploadDownloadStream 分支本就不创建临时目录。
            _ => false,
        };
        if (!shouldDelete) return;
        try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
        catch { /* 清理失败不影响上传结果；遗留目录由运维/系统临时目录回收 */ }
    }

    /// <summary>
    /// 把一个产物（文件或目录）展开成 (本机路径, 相对 workDir 的路径) 列表。
    /// 越出 <paramref name="workDir"/> 的路径一律跳过 —— 工具给出的路径不可全信。
    /// </summary>
    private static void CollectFiles(string source, string workDir, List<(string Local, string Relative)> sink)
    {
        var rootFull = Path.GetFullPath(workDir);
        string full;
        try { full = Path.GetFullPath(source); }
        catch { return; }

        // 只接受 workDir 之下的产物（防工具给出任意本机路径被搬运进用户目录）。
        if (!IsUnderDirectory(full, rootFull)) return;

        if (File.Exists(full))
        {
            sink.Add((full, Path.GetRelativePath(rootFull, full)));
            return;
        }
        if (!Directory.Exists(full)) return;

        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            sink.Add((file, Path.GetRelativePath(rootFull, file)));
    }

    /// <summary><paramref name="path"/> 是否位于 <paramref name="dir"/> 之下（含自身）。</summary>
    private static bool IsUnderDirectory(string path, string dir)
    {
        if (string.Equals(path, dir, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把「目标 VFS 目录 + 相对路径」拼成完整 VFS 路径。
    /// 相对路径来自 <see cref="Path.GetRelativePath"/>（Windows 上是反斜杠），必须转成 <c>/</c>。
    /// </summary>
    private static string CombineVfsPath(string destPath, string relative)
    {
        var rel = relative.Replace('\\', '/').Trim('/');
        var dest = destPath.TrimEnd('/');
        return string.IsNullOrEmpty(rel) ? dest : dest + "/" + rel;
    }

    /// <summary>VFS 路径的父目录（根为 <c>/</c>）。</summary>
    private static string ParentOf(string path)
    {
        var clean = path.TrimEnd('/');
        var idx = clean.LastIndexOf('/');
        return idx <= 0 ? "/" : clean[..idx];
    }

    /// <summary>「目录已存在」类错误（各驱动措辞不同，按关键字粗判即可）。</summary>
    private static bool IsAlreadyExists(string? message)
        => message is not null && message.Contains("exist", StringComparison.OrdinalIgnoreCase);

    /// <summary>从响应头/URL 推断远端文件名（只取最后一段，防路径分隔符注入）。</summary>
    private static string ResolveRemoteFileName(HttpResponseMessage resp, string url)
    {
        var candidate = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(candidate) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            candidate = uri.AbsolutePath;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            try { candidate = Uri.UnescapeDataString(candidate); }
            catch (UriFormatException) { /* 保持原样 */ }
            var name = Path.GetFileName(candidate);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return "download.bin";
    }

    // 从设置中读取 aria2 的地址与密钥，构建对应的 Aria2 适配器。
    private async Task<IOfflineDownloadTool> BuildAria2Async(CancellationToken ct)
    {
        var uri = await GetSettingAsync("aria2_uri", "");
        var secret = await GetSettingAsync("aria2_secret", "");
        var client = _http.CreateClient("offline_download_aria2");
        return new Aria2Tool(client, uri, secret);
    }

    // 从设置中读取 qBittorrent 的地址与凭据，构建对应的适配器。
    private async Task<IOfflineDownloadTool> BuildQbitAsync(CancellationToken ct)
    {
        var url = await GetSettingAsync("qbittorrent_url", "");
        var user = await GetSettingAsync("qbittorrent_user", "");
        var pass = await GetSettingAsync("qbittorrent_password", "");
        var client = _http.CreateClient("offline_download_qbit");
        return new QbitTool(client, url, user, pass);
    }

    // 从设置中读取 Transmission 的地址与凭据，构建对应的适配器。
    private async Task<IOfflineDownloadTool> BuildTransmissionAsync(CancellationToken ct)
    {
        var uri = await GetSettingAsync("transmission_uri", "");
        var user = await GetSettingAsync("transmission_user", "");
        var pass = await GetSettingAsync("transmission_password", "");
        var client = _http.CreateClient("offline_download_transmission");
        return new TransmissionTool(client, uri, user, pass);
    }

    // 构建服务端自带的直连下载器（无需配置）。
    private Task<IOfflineDownloadTool> BuildSimpleHttpAsync(CancellationToken ct)
    {
        var client = _http.CreateClient("offline_download_simple_http");
        return Task.FromResult<IOfflineDownloadTool>(new SimpleHttpTool(client));
    }

    // 在独立作用域中读取配置项；读取失败时回退到默认值，避免工具构建失败。
    private async Task<string> GetSettingAsync(string key, string fallback)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            var r = await svc.GetAsync(key);
            return (r.Code == 200 && r.Data is not null && !string.IsNullOrWhiteSpace(r.Data.Value))
                ? r.Data.Value
                : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>计数流：把「已读取字节数」回调出去，供边下边传的任务面板报进度。</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onRead;
        private long _total;

        public CountingStream(Stream inner, Action<long> onRead)
        {
            _inner = inner;
            _onRead = onRead;
        }

        /// <summary>已读取的总字节数。</summary>
        public long Total => Interlocked.Read(ref _total);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        /// <summary>
        /// 底层是网络流时 <c>Length</c> 通常直接抛 NotSupported；上传实现若据它
        /// 判断 Content-Length，抛异常会让整条上传失败，因此退化为 0（未知）。
        /// </summary>
        public override long Length
        {
            get { try { return _inner.Length; } catch { return 0; } }
        }
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0) Interlocked.Add(ref _total, n);
            _onRead(Total);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
            if (n > 0) Interlocked.Add(ref _total, n);
            _onRead(Total);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await _inner.ReadAsync(buffer, ct);
            if (n > 0) Interlocked.Add(ref _total, n);
            _onRead(Total);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
