using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Hosting;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 管理端点：存储 CRUD、驱动列表、用户管理、设置、元数据、站内信、扫描、搜索索引。
/// 对应 OpenList Go <c>server/handles/admin</c>。所有端点均要求 admin 角色。
/// 路由位于 <c>/api/admin</c> 下，整体 <c>RequireAuthorization("admin")</c>。
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    /// 注册 <c>/api/admin</c> 管理路由组，全部需要 admin 角色（含 /admin/task 兼容别名）。
    /// </summary>
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("admin");

        // --- 存储管理 ---
        // GET /admin/storage/list -> Resp<PageResp<StorageWithDriver>> = { content, total }
        g.MapGet("/storage/list", async (IStorageService svc) =>
            Results.Ok(await svc.ListAsync()));

        g.MapGet("/storage/get", async (long id, IStorageService svc) =>
            Results.Ok(await svc.GetAsync(id)));

        g.MapPost("/storage/create", async (CreateStorageReq req, IStorageService svc) =>
            Results.Ok(await svc.CreateAsync(req)));

        g.MapPost("/storage/update", async (CreateStorageReq req, IStorageService svc) =>
            Results.Ok(await svc.UpdateAsync(req.Id, req)));

        g.MapPost("/storage/delete", async (long id, IStorageService svc) =>
            Results.Ok(await svc.DeleteAsync(id)));

        g.MapPost("/storage/enable", async (long id, IStorageService svc) =>
            Results.Ok(await svc.EnableAsync(id)));

        g.MapPost("/storage/disable", async (long id, IStorageService svc) =>
            Results.Ok(await svc.DisableAsync(id)));

        g.MapPost("/storage/load_all", async (IStorageService svc) =>
            Results.Ok(await svc.LoadAllAsync()));

        // --- Driver listing ---
        g.MapGet("/driver/list", async (IStorageService svc) =>
            Results.Ok(await svc.ListDriversAsync()));

        g.MapGet("/driver/names", async (IStorageService svc) =>
            Results.Ok(await svc.ListDriverNamesAsync()));

        g.MapGet("/driver/info", async (string driver, IStorageService svc) =>
            Results.Ok(await svc.GetDriverInfoAsync(driver)));

        // --- 用户管理 ---
        // GET /admin/user/list -> { content, total } (frontend Users.tsx reads data.content)
        g.MapGet("/user/list", async (IUserService users) =>
        {
            var r = await users.ListAsync();
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data, total = r.Data.Count }));
        });

        g.MapGet("/user/get", async (long id, IUserService users) =>
            Results.Ok(await users.GetAsync(id)));

        g.MapPost("/user/create", async (CreateUserReq req, IUserService users) =>
            Results.Ok(await users.CreateAsync(req)));

        g.MapPost("/user/update", async (UpdateUserReq req, IUserService users) =>
            Results.Ok(await users.UpdateAsync(req.Id, req)));

        g.MapPost("/user/cancel_2fa", async (long id, IUserService users) =>
            Results.Ok(await users.Cancel2FaAsync(id)));

        g.MapPost("/user/delete", async (long id, IUserService users) =>
            Results.Ok(await users.DeleteAsync(id)));

        g.MapPost("/user/set_password", async (SetPasswordReq req, IUserService users) =>
            Results.Ok(await users.UpdateAsync(req.Id, new UpdateUserReq { Password = req.Password })));

        // POST /admin/user/reset_admin_password — 重置<em>内置</em>管理员的密码。
        // 与 set_password 的区别：不针对指定 id、不需要旧密码，只要调用者是 admin 即可。
        // 内置 admin 被刻意设为不可删除 / 不可改名 / 不可降权，若改密还要求旧密码，
        // 一旦忘记就彻底失去管理入口；此端点是桌面端与网页端的共同恢复通道。
        g.MapPost("/user/reset_admin_password", async (ResetAdminPasswordReq req, IUserService users, HttpContext ctx) =>
            Results.Ok(await users.ResetAdminPasswordAsync(req.Password, ctx.RequestAborted)));

        // POST /admin/user/del_cache?id= — clear a user's cached profile (no-op until
        // a cache layer is added, but the route is registered for frontend compat).
        g.MapPost("/user/del_cache", async (long id, IUserService users) =>
            Results.Ok(await users.DelCacheAsync(id)));

        // --- Admin SSH-key management (any user) ---
        // GET /admin/user/sshkey/list?uid={uid} — paged response { content, total }.
        g.MapGet("/user/sshkey/list", async (long uid, ISshKeyService svc) =>
        {
            var r = await svc.ListAsync(uid);
            if (r.Code != 200) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data!, total = r.Data!.Count }));
        });

        // POST /admin/user/sshkey/delete?uid={uid}&id={id} — returns remaining keys.
        g.MapPost("/user/sshkey/delete", async (long uid, long id, ISshKeyService svc) =>
            Results.Ok(await svc.DeleteAsync(uid, id, isAdmin: true)));

        // --- Setting management ---
        // GET /admin/setting/list?group=N  or  ?groups=N1,N2  — filter by group.
        // group values are the frontend's Group enum (numeric).
        g.MapGet("/setting/list", async (string? group, string? groups, ISettingService svc) =>
        {
            var r = await svc.ListAsync();
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);

            var wanted = new HashSet<int>();
            if (!string.IsNullOrEmpty(group) && int.TryParse(group, out var g1)) wanted.Add(g1);
            if (!string.IsNullOrEmpty(groups))
            {
                foreach (var g in groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(g, out var gn)) wanted.Add(gn);
            }
            if (wanted.Count == 0) return Results.Ok(r);

            var filtered = r.Data.Where(s => wanted.Contains(s.Group)).ToList();
            return Results.Ok(Resp<List<SettingItem>>.Ok(filtered));
        });

        g.MapPost("/setting/save", async (List<SettingItem> items, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync(items)));

        g.MapGet("/setting/get", async (string keys, ISettingService svc) =>
        {
            var results = new List<SettingItem>();
            foreach (var key in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var r = await svc.GetAsync(key);
                if (r.Code == 200 && r.Data is not null) results.Add(r.Data);
            }
            return Results.Ok(Resp<List<SettingItem>>.Ok(results));
        });

        g.MapPost("/setting/set", async (List<SettingItem> items, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync(items)));

        // DELETE /admin/setting/delete?key= — remove a single setting key.
        g.MapPost("/setting/delete", async (string key, ISettingService svc) =>
        {
            if (string.IsNullOrEmpty(key)) return Results.Ok(Resp.Fail("key is required", RespCodes.BadRequest));
            return Results.Ok(await svc.DeleteAsync(key));
        });

        // POST /admin/setting/default?group= — restore defaults for a settings group.
        g.MapPost("/setting/default", async (HttpRequest req, ISettingService svc) =>
        {
            var group = req.Query["group"].ToString();
            return Results.Ok(await svc.DefaultAsync(string.IsNullOrEmpty(group) ? null : group));
        });

        // POST /admin/setting/reset_token — rotate the JWT signing secret. All existing
        // tokens are invalidated. The service updates the singleton AppConfiguration
        // (used by JwtService/SignService) and persists the new secret; here we also
        // update the bearer middleware's validation parameters so validation uses the
        // new secret immediately (without an app restart).
        g.MapPost("/setting/reset_token", async (
            ISettingService svc,
            IOptionsMonitor<JwtBearerOptions> bearerOpts,
            JwtService jwt) =>
        {
            var r = await svc.ResetTokenAsync();
            if (r.Code == 200)
                bearerOpts.CurrentValue.TokenValidationParameters = jwt.BuildValidationParameters();
            return Results.Ok(r);
        });

        // --- Offline-download-tool config routes ---
        // Each route mirrors the Go /admin/setting/set_{tool} endpoints used by the
        // frontend's Other.tsx. They accept a tool-specific body and persist the
        // values via SaveAsync under the canonical setting keys.
        g.MapPost("/setting/set_aria2", async (SetAria2Req req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "aria2_uri", Value = req.Uri ?? "" },
                new() { Key = "aria2_secret", Value = req.Secret ?? "" },
            ])));

        g.MapPost("/setting/set_qbit", async (SetQbitReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "qbittorrent_url", Value = req.Url ?? "" },
                new() { Key = "qbittorrent_seedtime", Value = req.Seedtime ?? "" },
            ])));

        g.MapPost("/setting/set_transmission", async (SetTransmissionReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "transmission_uri", Value = req.Uri ?? "" },
                new() { Key = "transmission_seedtime", Value = req.Seedtime ?? "" },
            ])));

        g.MapPost("/setting/set_123_pan", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "123_pan_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_115", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "115_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_115_open", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "115_open_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_123_open", async (Set123OpenReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "123_open_temp_dir", Value = req.TempDir ?? "" },
                new() { Key = "123_open_callback_url", Value = req.CallbackUrl ?? "" },
            ])));

        g.MapPost("/setting/set_pikpak", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "pikpak_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_thunder", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "thunder_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_thunderx", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "thunderx_temp_dir", Value = req.TempDir ?? "" },
            ])));

        g.MapPost("/setting/set_thunder_browser", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "thunder_browser_temp_dir", Value = req.TempDir ?? "" },
            ])));

        // Mirrors Go router.go: POST /admin/setting/set_guangyapan (body { temp_dir }).
        g.MapPost("/setting/set_guangyapan", async (SetTempDirReq req, ISettingService svc) =>
            Results.Ok(await svc.SaveAsync([
                new() { Key = "guangyapan_temp_dir", Value = req.TempDir ?? "" },
            ])));

        // --- Meta management ---
        // Mirrors the frontend's Metas.tsx / AddOrEdit.tsx:
        //   GET    /admin/meta/list              -> { content, total }
        //   GET    /admin/meta/get?id=           -> Meta
        //   POST   /admin/meta/create            -> Meta
        //   POST   /admin/meta/update            -> {}
        //   POST   /admin/meta/delete?id=        -> {}
        g.MapGet("/meta/list", async (IMetaService svc) =>
        {
            var r = await svc.ListAsync();
            if (r.Code != 200 || r.Data is null) return Results.Ok(r);
            return Results.Ok(Resp<object>.Ok(new { content = r.Data, total = r.Data.Count }));
        });

        g.MapGet("/meta/get", async (long id, IMetaService svc) =>
            Results.Ok(await svc.GetByIdAsync(id)));

        g.MapPost("/meta/create", async (Meta meta, IMetaService svc) =>
            Results.Ok(await svc.CreateAsync(meta)));

        g.MapPost("/meta/update", async (Meta meta, IMetaService svc) =>
            Results.Ok(await svc.UpdateAsync(meta.Id, meta)));

        // Back-compat: /meta/save dispatches create vs update by Id.
        g.MapPost("/meta/save", async (Meta meta, IMetaService svc) =>
            meta.Id == 0
                ? Results.Ok(await svc.CreateAsync(meta))
                : Results.Ok(await svc.UpdateAsync(meta.Id, meta)));

        g.MapPost("/meta/delete", async (long id, IMetaService svc) =>
            Results.Ok(await svc.DeleteAsync(id)));

        // --- Message management ---
        // Persistent admin-to-user messages (CRUD).
        g.MapGet("/message/list", async (long user_id, IMessageService svc) =>
            Results.Ok(await svc.GetAsync(user_id)));

        // GET /admin/message/get?id= — fetch a single persistent message by id.
        g.MapGet("/message/get", async (long id, IMessageService svc) =>
            Results.Ok(await svc.GetByIdAsync(id)));

        g.MapPost("/message/create", async (CreateMessageReq req, IMessageService svc) =>
            Results.Ok(await svc.SendAsync(req.UserId, req.Title, req.Content)));

        // Alias: frontend Messenger uses /admin/message/send with { message } body for
        // admin broadcast (in-memory). The frontend sends a plain string
        // (r.post("/admin/message/send", { message: toSend() })) so wrap it into a
        // BroadcastMessage { type: "string", content: <str> } — mirroring Go's
        // message.SendHandle which accepts Req{Message string}.
        g.MapPost("/message/send", async (SendBroadcastReq? req, IBroadcastService svc) =>
        {
            var msg = req?.Message switch
            {
                null => new BroadcastMessage(),
                string s => new BroadcastMessage { Type = "string", Content = s },
                JsonElement e when e.ValueKind == JsonValueKind.String => new BroadcastMessage { Type = "string", Content = e.GetString() },
                BroadcastMessage bm => bm,
                _ => new BroadcastMessage { Type = "string", Content = req!.Message },
            };
            return Results.Ok(await svc.SendAsync(msg));
        });

        g.MapPost("/message/update", async (long id, IMessageService svc) =>
            Results.Ok(await svc.MarkReadAsync(id)));

        // POST /admin/message/delete?id= — delete a persistent message.
        g.MapPost("/message/delete", async (long id, IMessageService svc) =>
            Results.Ok(await svc.DeleteAsync(id)));

        // POST /admin/message/get (no body) — retrieve the latest admin broadcast.
        // The frontend Messenger polls this to display site-wide announcements.
        g.MapPost("/message/get", async (IBroadcastService svc) =>
            Results.Ok(await svc.GetAsync()));

        // --- Scan ---
        // --- Scan management ---
        // POST /admin/scan/start — body { path, limit }. limit is a rate limit
        // (files/sec, 0 = unlimited); currently ignored by ScanService but accepted
        // for frontend compatibility.
        g.MapPost("/scan/start", async (ScanStartReq req, IScanService svc) =>
            Results.Ok(await svc.StartAsync(req.Path ?? "/", default)));

        // POST /admin/scan/stop — cancel a running scan.
        g.MapPost("/scan/stop", async (IScanService svc) =>
            Results.Ok(await svc.StopAsync()));

        // GET /admin/scan/progress — returns { obj_count, is_done } shape expected
        // by the frontend. is_done is true when no scan is running.
        g.MapGet("/scan/progress", async (IScanService svc) =>
        {
            var r = await svc.GetProgressAsync();
            if (r.Code != 200) return Results.Ok(r);
            var p = r.Data!;
            return Results.Ok(Resp<object>.Ok(new
            {
                obj_count = p.Scanned,
                is_done = p.State != "running",
            }));
        });

        // GET /admin/system/info — 管理台「关于」页的数据源（见 docs §38）。
        //
        // 放在 admin 组里而不是公开端点：里面有数据库文件路径、日志路径、挂载目录这类
        // 内部布局信息，对排查「前端是不是旧产物」「连的是哪个库」有用，但对匿名访客只是
        // 免费的情报。也因此**不返回**数据库口令（cfg.Database.Password 一个字节都不出去）。
        //
        // 字段名用 snake_case：与紧邻的 /index/progress、/scan/progress 同一族，前端
        // 直接照抄键名。注意全局 JsonNamingPolicy.CamelCase 对已是小写开头的键是**空操作**
        //（它只处理首字母大写的情况），所以 snake_case 能原样出去。
        g.MapGet("/system/info", (AppConfiguration cfg) =>
        {
            var distDir = TryFullPath(cfg.DistDir);
            DateTimeOffset? distBuiltAt = null;
            if (distDir is not null)
            {
                try
                {
                    var index = new FileInfo(Path.Combine(distDir, "index.html"));
                    if (index.Exists) distBuiltAt = new DateTimeOffset(index.LastWriteTimeUtc, TimeSpan.Zero);
                }
                catch (IOException) { /* 路径存在但读不了：当作「没有产物」处理 */ }
                catch (UnauthorizedAccessException) { }
            }

            var isSqlite = !cfg.Database.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase)
                           && !cfg.Database.Type.Equals("postgresql", StringComparison.OrdinalIgnoreCase);
            var dbFile = isSqlite ? TryFullPath(cfg.Database.DBFile) : null;
            var startedAt = BuildInfo.ProcessStartedAtUtc();
            var now = DateTimeOffset.UtcNow;

            return Results.Ok(Resp<object>.Ok(new
            {
                version = BuildInfo.Version,
                tag = BuildInfo.Tag,
                commit = BuildInfo.Commit,
                configuration = BuildInfo.Configuration,
                built_at = BuildInfo.AssemblyBuiltAtUtc()?.ToString("O") ?? "",
                runtime = BuildInfo.Framework,
                os = BuildInfo.Os,
                architecture = BuildInfo.Architecture,
                processor_count = Environment.ProcessorCount,
                started_at = startedAt?.ToString("O") ?? "",
                uptime_seconds = startedAt is null ? -1 : (long)(now - startedAt.Value).TotalSeconds,
                server_time = now.ToString("O"),
                database = new
                {
                    type = isSqlite ? "sqlite" : "postgres",
                    // 口令绝不外泄；其余字段只用于回答「这台机器连的是哪个库」
                    file = dbFile ?? "",
                    host = isSqlite ? "" : cfg.Database.Host,
                    port = isSqlite ? 0 : cfg.Database.Port,
                    name = isSqlite ? "" : cfg.Database.Name,
                },
                paths = new
                {
                    data_dir = dbFile is null ? "" : (Path.GetDirectoryName(dbFile) ?? ""),
                    dist_dir = distDir ?? "",
                    // index.html 的写入时间 ≈ 前端产物的构建时间；与 built_at 一起看就能
                    // 判断「后端换了、前端还是旧的」这种最常见的部署事故
                    dist_built_at = distBuiltAt?.ToString("O") ?? "",
                    temp_dir = TryFullPath(cfg.TempDir) ?? "",
                    log_file = cfg.Log.Enable ? TryFullPath(cfg.Log.Name) ?? "" : "",
                    log_enabled = cfg.Log.Enable,
                },
                features = new
                {
                    s3 = cfg.S3.Enable,
                    ftp = cfg.FTP.Enable,
                    sftp = cfg.SFTP.Enable,
                    ldap = !string.IsNullOrWhiteSpace(cfg.Ldap.Url),
                    mcp = cfg.MCP.Enable,
                    search_index = cfg.SearchIndex,
                    max_connections = cfg.MaxConnections,
                },
            }));
        });

        // --- Search index management ---
        // POST /admin/index/build — body { paths, max_depth }. Full rebuild.
        g.MapPost("/index/build", async (IndexBuildReq req, ISearchService svc) =>
            Results.Ok(await svc.BuildIndexAsync(req.Paths ?? [], req.MaxDepth)));

        // POST /admin/index/update — body { paths, max_depth }. Incremental update.
        g.MapPost("/index/update", async (IndexBuildReq req, ISearchService svc) =>
            Results.Ok(await svc.UpdateIndexAsync(req.Paths ?? [], req.MaxDepth)));

        // POST /admin/index/stop — cancel a running index build.
        g.MapPost("/index/stop", async (ISearchService svc) =>
            Results.Ok(await svc.StopIndexAsync()));

        // POST /admin/index/clear — clear the index and reset progress.
        g.MapPost("/index/clear", async (ISearchService svc) =>
            Results.Ok(await svc.ClearIndexAsync()));

        // GET /admin/index/progress — returns { obj_count, is_done, last_done_time, error }
        // matching the frontend's Progress type.
        g.MapGet("/index/progress", async (ISearchService svc) =>
        {
            var r = await svc.GetProgressAsync();
            if (r.Code != 200) return Results.Ok(r);
            var p = r.Data!;
            return Results.Ok(Resp<object>.Ok(new
            {
                obj_count = p.Indexed,
                is_done = p.State != "running",
                last_done_time = p.LastDoneTime?.UtcDateTime.ToString("O") ?? "",
                error = p.Error ?? "",
            }));
        });

        // retain /admin/task API to ensure compatibility with legacy automation
        // scripts (mirrors Go router.go: _task(g.Group("/task")) under the admin group).
        g.MapGroup("/task").MapTaskRoutes();

        return app;
    }

    /// <summary>
    /// 把配置里的相对路径解析成绝对路径，失败返回 <c>null</c>。
    ///
    /// 存在的意义是 <see cref="Path.GetFullPath(string)"/> 会对非法字符/超长路径抛异常 ——
    /// 「关于」页只是展示信息，不该因为一个配错的 <c>TempDir</c> 让整页 500。
    /// </summary>
    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }
}

/// <summary>设置用户密码的请求体（<c>POST /admin/user/set_password</c>）。</summary>
public sealed class SetPasswordReq { public long Id { get; set; } public string Password { get; set; } = ""; }
/// <summary>重置内置管理员密码的请求体（<c>POST /admin/user/reset_admin_password</c>）。</summary>
public sealed class ResetAdminPasswordReq { public string Password { get; set; } = ""; }
/// <summary>创建站内信的请求体（<c>POST /admin/message/create</c>）。</summary>
public sealed class CreateMessageReq { public long UserId { get; set; } public string Title { get; set; } = ""; public string Content { get; set; } = ""; }
/// <summary>广播消息请求体（<c>POST /admin/message/send</c>）；Message 兼容字符串或结构化对象。</summary>
public sealed class SendBroadcastReq { public object? Message { get; set; } }
/// <summary>启动扫描的请求体（<c>POST /admin/scan/start</c>）；Limit 为限速（文件/秒，0=不限）。</summary>
public sealed class ScanStartReq { public string? Path { get; set; } public double Limit { get; set; } }
/// <summary>构建/更新搜索索引的请求体（<c>POST /admin/index/build</c> 或 <c>/admin/index/update</c>）。</summary>
public sealed class IndexBuildReq
{
    public List<string>? Paths { get; set; }
    [JsonPropertyName("max_depth")]
    public int MaxDepth { get; set; } = -1;
}

// --- 离线下载工具配置请求 DTO（对应前端 Other.tsx） ---
/// <summary>aria2 配置体（<c>POST /admin/setting/set_aria2</c>）。</summary>
public sealed class SetAria2Req { public string? Uri { get; set; } public string? Secret { get; set; } }
/// <summary>qBittorrent 配置体（<c>POST /admin/setting/set_qbit</c>）。</summary>
public sealed class SetQbitReq { public string? Url { get; set; } public string? Seedtime { get; set; } }
/// <summary>Transmission 配置体（<c>POST /admin/setting/set_transmission</c>）。</summary>
public sealed class SetTransmissionReq { public string? Uri { get; set; } public string? Seedtime { get; set; } }
/// <summary>临时目录配置体（多个 set_xxx 共用）。</summary>
public sealed class SetTempDirReq { public string? TempDir { get; set; } }
/// <summary>123 云盘（开放平台）配置体（<c>POST /admin/setting/set_123_open</c>）。</summary>
public sealed class Set123OpenReq { public string? TempDir { get; set; } public string? CallbackUrl { get; set; } }
