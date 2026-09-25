# OpenList.Net — Agent Instructions

## Build & Test

- **Build**: `dotnet build`
- **Run**: `dotnet run --project src/OpenList.Server` (listens `http://0.0.0.0:5244` by default)
- **Test**: `dotnet test` — uses **TUnit** (not NUnit), async convention: `await Assert.That(actual).Is(expected)`
- **Lint**: `dotnet format`
- **Cake build** (file-based `Cake.Sdk` script, `build/build.cs`): `dotnet run build/build.cs -- --target=<Task>` (e.g. `--target=CI`, `--target=Publish --semver=1.2.3`). `Cake.Sdk` is pinned via `global.json` → `msbuild-sdks`. See `build/build.cs` for all targets/args.
  - **Publish variants** via `--app=`: `server` (default — headless server; framework-dependent without `--rid`, self-contained single-file with it) and `desktop` (`--app=desktop --rid=<rid>` — Avalonia GUI at bundle root + single-file server under `server/` + `frontend/dist/`; always self-contained). Bundle names: `openlist-net[-desktop]-<rid>[-semver]`.
- **Dev server** (with frontend proxy): `dotnet run` in server + `pnpm dev` in `frontend/`

## Project Layout

| Project | Purpose |
|---|---|
| `src/OpenList.Core` | Models, `ZioDriverBase`, driver extension interfaces, service interfaces. Depends only on Zio + ASP.NET abstractions |
| `src/OpenList.Drivers` | Storage driver implementations (`Local/`, `S3/`, `WebDAV/`, `FtpSftpSmb/`, `Cloud/`) |
| `src/OpenList.Infrastructure` | EF Core `DbContext`, service impls, JWT auth, `ZioMountService`. References Core |
| `src/OpenList.Server` | ASP.NET entrypoint (`Program.cs`), `Endpoints/` (Minimal APIs), `Middleware/`, `Models/` (DTOs + `Resp<T>`) |
| `tests/OpenList.Tests` | TUnit tests for Core + Infrastructure services |
| `frontend/` | SolidJS SPA (separate project, pnpm, proxies `/api` to `localhost:5244`) |
| `src/OpenList.Desktop` | AvaloniaUI 12 desktop app (mirrors OpenList-Desktop): manages the OpenList core as a child process (`ASPNETCORE_URLS` overrides port), rclone WebDAV mounts, tray icon, live log viewer. No file-management UI (removed; fs API methods dropped from `OpenListApiClient`). Settings persisted to `%APPDATA%/OpenList.Desktop/settings.json` |

## API Patterns

- **Minimal APIs only** — all endpoints in `src/OpenList.Server/Endpoints/` as static extension methods (`MapAuthApi`, `MapFsApi`, etc.), registered in `Program.cs`
- **camelCase JSON** (`JsonNamingPolicy.CamelCase`)
- All responses wrapped in `Resp<T>`:
  ```json
  { "code": 200, "message": "success", "data": ... }
  ```
- Object mapping done manually (DTO ↔ entity). **Mapster is NOT actually used** — the package is referenced but `MappingConfig.Configure()` is never called / no `.Adapt<>` usage. Do not rely on it.
- Health check: `GET /ping` → `"pong"`

## Endpoint Routes

| File | Route Group | Auth |
|---|---|---|
| `AuthEndpoints.cs` | `/api/auth` | Mixed |
| `AuthEndpoints.cs` (AuthnEndpoints) | `/api/authn` | Mixed (WebAuthn, implemented) |
| `FsEndpoints.cs` | `/api/fs` | Auth'd |
| `AdminEndpoints.cs` | `/api/admin` | Admin (`RequireRole("2")`) |
| `TaskEndpoints.cs` | `/api/task` | Auth'd |
| `MeEndpoints.cs` | `/api/me` | Auth'd |
| `PublicEndpoints.cs` | `/api/public` | Public |
| `SignEndpoints.cs` | `/d/`, `/p/` | Public (sign-verified) |
| `HealthEndpoints.cs` | `/ping` | Public |
| `S3Endpoints.cs` | `/s3` | Mixed |
| `WebDavEndpoints.cs` | `/dav` | Mixed |

## Driver System

- **Auto-discovery**: `ZioMountService` scans assemblies starting with `"OpenList"` for `ZioDriverBase` subclasses. Adding a driver = create a class in `OpenList.Drivers` — no manual registration.
- **Zio-based**: All drivers extend Zio's `FileSystem`, mounted via `MountFileSystem`. Extension interfaces: `IFileSystemLinkProvider`, `IFileSystemUploader`, `IFileSystemWithDetails`, `IFileSystemArchiveReader`, `IFileSystemArchiveDecompress`, `IFileSystemDirectUploader`.
- **Driver config schema is backend-driven**: drivers declare `DriverItemAttributes` via `ConfigItems`; `StorageService.BuildDriverInfo` splits them into `common` (generated) + `additional` (from `ConfigItems`) and the frontend renders the form from `GET /admin/driver/list`. Field names double as translation keys in `frontend/src/lang/en/drivers.json` — a mismatch shows raw untranslated keys in the UI.
- **Runtime context injection**: `ZioMountService.LoadAsync` calls `driver.AttachRuntime(mountPath, additionSink)` **before** `InitAsync`, giving the driver a `MountPath` and an `IStorageAdditionSink`.
- **Addition write-back** (`IStorageAdditionSink` / `StorageAdditionSink`): mirror of Go `op.MustSaveDriverStorage`. Drivers call `ZioDriverBase.SaveAdditionAsync(addition)` to persist runtime config changes (chiefly refreshed OAuth tokens) back to `x_storages.Addition`. Serialization uses the object's **runtime type** so `[JsonPropertyName]` snake_case keys round-trip with `DriverConfig.GetAddition<T>()`. The sink is a singleton that opens its own DI scope per write (scoped `OpenListDbContext`); it short-circuits when the JSON is unchanged to avoid write amplification.
- **`LoadAsync` semantics**: a driver that returns successfully but leaves `System == null` is **not mounted** (config still saves) — that's how the AliyundriveOpen skeleton behaves. `LoadAllAsync` catches per-storage init failures and continues (mirrors Go `op.initStorage`); without this, one broken storage killed app startup.
- **Cloud driver base** (`OpenList.Drivers/Cloud/`, mirrors Go `drivers/aliyundrive_open/{limiter,util}.go`):
  - `CloudDriverBase` — sets `IsCloudDriver => true`, exposes a shared `HttpClient` (`CloudHttpClient.Shared`: auto-decompress, 30 s connect timeout, infinite overall timeout, 64 conns/host — a process-level singleton because drivers are created by `Activator.CreateInstance` with no DI), plus `ConfigureRateLimit(rps)` / `WaitRateLimitAsync`. Its `RefreshAndPersistTokensAsync` does refresh → apply → persist, in that order (so a DB write failure doesn't leave the live instance on a stale token).
  - `RequestRateLimiter` — token bucket with capacity 1, matching Go `rate.NewLimiter(rate.Limit(rps), 1)`. `limit_rate` is a **float in requests/second** (`<= 0` = unlimited). Concurrent waits are serialized so bursts can't exceed the limit.
  - `OAuthTokenRefresher` — retry loop mirroring Go's `refreshToken()` (1 initial + 3 retries = 4 default `MaxAttempts`), `ForJsonEndpoint(...)` helper for the common "POST JSON → `{access_token, refresh_token}`" shape. Treats **HTTP 200 with an empty `access_token` as a failure** (services do this when the refresh token is dead; writing that empty token would brick the storage), doesn't retry on caller cancellation, and includes the last error message in the thrown exception.
  - `OAuthTokens` — `access_token` / `refresh_token` pair.
  - `CallbackLimiterRegistry` / `CallbackLimiterRegistration` / `CallbackPermit` (`CallbackConcurrencyLimiter.cs`, mirrors Go `drivers/aliyundrive_open/callback.go`) — process-level proxy-callback admission registry **shared by Aliyun user id**; effective limit is the min of live registrations (`<=0` normalized to 1). Permits are held for the **whole response-body lifetime**, not just headers; acquire waits up to 1 s then throws `CallbackCapacityException` (Go `errs.TemporaryCapacity`, maps to S3 SlowDown later). Registry entries are reclaimed once a user has no registrations and no active permits. AliyundriveOpen wires it via `AttachCallbackLimiter(userId)` (call site arrives with the API port; skeleton has no user id yet) and unregisters on Dispose.
  - `CallbackRangeReader` (`CallbackRangeReader.cs`) — ranged reader over the Aliyun callback URL. Retries **only** verified capacity rejections (HTTP 403 + `RequestDeniedByCallback` + `ExceedMaxConcurrency`, see `IsCapacityRejection`), 3 attempts with exponential backoff + jitter, then `CallbackCapacityException`. Accepts 206 / full-range 200 / `Content-Range` start match; slices locally (`SkipLimitStream`) when the server ignores Range. Other errors fail immediately with the callback URL redacted from the message. Tests inject a fake `HttpMessageHandler`; `AcquireTimeout`/`RetryBaseDelay` are internal seams.

## Key Quirks

- **Auth**: JWT Bearer with configurable secret; auto-generated as 32 hex chars (2×Guid) if empty in config. Frontend sends token without `"Bearer "` prefix (custom `OnMessageReceived` handler).
- **Database**: defaults to `sqlite` (`data/data.db`); also supports `postgres`/`postgresql`. Table prefix: `x_`. Init: `MigrateAsync()` → falls back to `EnsureCreatedAsync()`. Add design-time migrations via `OpenListDbContextFactory` (uses SQLite).
- **CORS**: `AllowAnyOrigin/Method/Header` + expose `Content-Range`/`Content-Disposition` headers. Uses built-in `app.UseCors()` (NOT a custom middleware).
- **Rate limiters**: byte-level token bucket (mirrors Go `rate.Limiter` + `stream`). `RateLimiter` (per-limiter, `<=0` = unlimited, initial bucket full, token debt allowed), `RateLimitManager` (singleton holding client/server × download/upload limiters, refreshed from settings table), `RateLimitMiddleware` (wraps download/upload routes' response/request streams), `MaxAllowedMiddleware` (global concurrency gate when `MaxConnections > 0`). Middleware order: ErrorHandling → RequestLog → static → CORS → auth → MaxAllowed → RateLimit(download) → RateLimit(upload).
- **Request logging**: Serilog configured in `Program.cs` — console sink always on; file sink with `Log.MaxSize/MaxBackups/MaxAge` (Go lumberjack-style rotation) when `Log.Enable`. `RequestLogMiddleware` filters per `Log.Filter` (Enable + CIDR/path/method rules; path prefix when starts with `/`, else contains; all conditions ANDed).
- **Error handling**: `ErrorHandlingMiddleware` (outermost) converts unhandled exceptions to `Resp<T>` JSON — 500 "internal server error", `BadHttpRequestException` → 400. Mirrors Go `gin.Recovery`.
- **Config validation**: `AppConfiguration.Validate()` runs at startup and throws on invalid db type / HTTP·HTTPS port / MaxConnections / search_index backend / log params.
- **Swagger**: at `/swagger` + Scalar at `/scalar` (both Development only; Scalar points at `/swagger/v1/swagger.json`).
- **FTP/SFTP servers**: hosted services, disabled by default (`FTP.Enable`/`SFTP.Enable` in config).
- **Kestrel body limit**: 10 GB (for WebDAV uploads).
- **SPA fallback**: serves `index.html` from `DistDir` (`../../frontend/dist` by default).
- **Environment variables**: use `OpenList__` prefix (e.g. `OpenList__Database__Type=postgres`).

## Not Yet Implemented (from Go reference)

### Stubbed (route exists, returns 501 "not implemented")

- `/api/fs/torrent/*` — 8 stubs: legacy `/list`, `/info`, `/add`, `/cancel` (old names) + Go-named `/parse`, `/upload_parse`, `/rapid_upload`, `/generate` (frontend uses the Go names). No BitTorrent engine integrated — all return `code=501`.
- `IFileSystemDirectUploader` / `IFileSystemWithDetails` — driver extension interfaces still unimplemented by any driver.
- `/debug/pprof/*` — Go's pprof surface has no .NET equivalent (DebugEndpoints registers `/debug/{gc,hide_privacy,path}` only).

> **SSO / WebAuthn / LDAP are NOT stubs anymore** — implemented: `/api/auth/sso`, `/sso_callback`, `/get_sso_id`, `/sso_get_token` (via `ISsoService`; 403 when disabled), `/api/authn/webauthn_{begin,finish}_{login,registration}` + `/delete_authn` + `/getcredentials` (via `IWebAuthnService`; 403 when disabled), `/api/auth/login/ldap` (via `JwtService.LoginLdapAsync`; 501 when LDAP unconfigured).

### Completely missing (no route registered)

**Sharing system** (Program.cs registers `MapShareApi` + `MapSharingDownloadApi`):
- ~~`/api/share/*` — 7 sharing-management routes~~ **Implemented** (`list`, `get`, `create`, `update`, `delete`, `enable`, `disable`). Lookups use the user-visible share **code** (frontend `?id=`), not the db primary key. Service: `SharingService` / `ISharingService`.
- ~~`/sd/:sid[/*path]` — sharing signed download (GET + HEAD)~~ **Implemented** (`ShareDownloadEndpoints.MapSharingDownloadApi`). Public; verifies share code + optional `?pwd=` + expiration/disable/max-accessed; bumps `Accessed` counter; streams file via `FileDownloadHelper` (Range-supported). Path mapping: `share.Path` + URL relative path (single-file/dir shares; multi-file matching is P2).
- ~~`/sad/:sid[/*path]` — sharing signed archive extract (GET + HEAD)~~ **Implemented** — delegates to `ArchiveDecompressAsync`; 501 until a driver implements `IFileSystemArchiveDecompress` (LocalDriver now does via SharpCompress).

**Signed archive routes** (Go `/ad/`, `/ap/`, `/ae/`):
- ~~`/ad/*path` — archive signed download (GET + HEAD)~~ **Implemented** (`SignEndpoints`, P1-5) — reuses `ISignService` + delegates `ArchiveListAsync`/`ArchiveDecompressAsync`; 501 when the driver has no archive support.
- ~~`/ap/*path` — archive signed proxy (GET + HEAD)~~ **Implemented** (same).
- ~~`/ae/*path` — archive signed internal extract (GET + HEAD)~~ **Implemented** (same).

~~**Sign endpoint HEAD methods**: Go registers `HEAD /d/*path` and `HEAD /p/*path`; .NET only has `GET`.~~ **Implemented** — `SignEndpoints` now registers `HEAD /d/{**path}` and `HEAD /p/{**path}` via `MapMethods`, used by media players/download managers to probe size/Range support.

**Auth/Me routes**:
- `/api/auth/login/ldap` — LDAP login
- ~~`/api/me/sshkey/{list,add,delete}` — SSH public-key management~~ **Implemented** (`MeEndpoints` + `SshKeyService`): `GET /me/sshkey/list` returns paged `{content, total}`; `POST /me/sshkey/add` body `{title, key}` computes `SHA256:base64` fingerprint; `POST /me/sshkey/delete?id=` returns remaining keys. `SshKey` model gained `LastUsedTime` (migration via `ALTER TABLE x_sshkeys ADD COLUMN`).
- ~~`/api/authn/{delete_authn,getcredentials}` — WebAuthn credential delete/list~~ **Implemented** (`AuthEndpoints` AuthnEndpoints): `POST /delete_authn`, `GET /getcredentials` via `IWebAuthnService` (403 when WebAuthn disabled).

**Fs routes**:
- ~~`/api/fs/regex_rename` — regex batch rename~~ **Implemented** (`POST /fs/regex_rename`, body `{src_dir, src_name_regex, new_name_regex}` → `FileSystemService.RegexRenameAsync`). The frontend computes names client-side and uses `/batch_rename`, so this is Go-API completeness.
- ~~`/api/fs/recursive_move` — recursive move~~ **Implemented** (`FsEndpoints.RecursiveMoveAsync`; Go alias for single-src move)
- ~~`/api/fs/form` — form upload~~ **Implemented** (`PUT /api/fs/form`, multipart; path from `File-Path` header. `/put` still handles stream uploads)
- ~~`/api/fs/get_direct_upload_info` — direct (client-side) upload info~~ **Implemented** (`POST /api/fs/get_direct_upload_info`)
- ~~`/api/fs/batch_rename`~~ **Implemented** (`POST /fs/batch_rename` body `{src_dir, rename_objects:[{src_name,new_name}]}`; prepends `src_dir` to form full paths, then calls `BatchRenameAsync`. The older `/rename_batch` endpoint with `{items:[{src_name,new_name}]}` full paths is retained for API compat.)
- `/api/fs/other` — misc fs operations

**Admin routes**:
- ~~`/api/admin/user/del_cache` — clear user cache~~ **Implemented** (`POST /admin/user/del_cache?id=`; `UserService.DelCacheAsync` is a no-op placeholder until a cache layer is added).
- ~~`/api/admin/user/sshkey/{list,delete}` — admin SSH-key management~~ **Implemented** (`AdminEndpoints`): `GET /admin/user/sshkey/list?uid=` and `POST /admin/user/sshkey/delete?uid=&id=`; reuses `SshKeyService` with `isAdmin: true` so admins can manage any user's keys.
- ~~`/api/admin/setting/delete`, `/setting/default`, `/setting/reset_token`~~ **Implemented** (`AdminEndpoints`): `delete` removes a key; `default?group=` restores `DbInitializer.DefaultSettings` for the group; `reset_token` rotates the JWT secret (updates singleton `AppConfiguration` + persists to `jwt_secret` file + updates bearer `TokenValidationParameters` via `IOptionsMonitor` so existing tokens are invalidated immediately).
- ~~`/api/admin/setting/set_{aria2,qbit,transmission,115,115_open,123_pan,123_open,pikpak,thunder,thunderx,thunder_browser}` — 11 offline-download-tool config routes~~ **Implemented** — 12 routes incl. `set_guangyapan` (`guangyapan_temp_dir`).
- ~~`/api/admin/index/{build,update,stop,clear,progress}` — search-index management (5 routes)~~ **Implemented** — `SearchService` (singleton) walks the filesystem via `IFileSystemService.ListAsync` in a fire-and-forget `Task.Run`, tracking progress in-memory. Routes: `POST /admin/index/{build,update,stop,clear}` (body `{paths, max_depth}` for build/update), `GET /admin/index/progress` returns `{obj_count, is_done, last_done_time, error}` matching the frontend `Progress` type. `ISearchService`/`IndexProgress` updated: `BuildIndexAsync`/`UpdateIndexAsync` now take `(IReadOnlyList<string> paths, int maxDepth)`; `IndexProgress` gained `LastDoneTime` + `Error`. Index entries ARE persisted when `search_index=database` (table `x_search_index`); `bleve` backend not implemented — `search_index=bleve` falls back to live filesystem `SearchAsync`. **Incremental indexing IS now implemented** (Phase 13): `auto_update_index` setting gates it; `FileSystemService` publishes list/delete events to `IObjsUpdateHooks` (singleton publish/subscribe registry), `SearchService` subscribes in its constructor and does localized `UpdateAsync`/remove-trees. Startup calls `ISearchService.InitIndexAsync()` (mirrors Go `bootstrap.InitIndex`) — restores persisted `last_done_time`, resets a crash-left `running` state to `idle`; no full auto-rebuild on storage load (same as Go). **Search access-control filtering** mirrors Go `search.go` SearchFiltered: `SearchAsync(req, user)` filters results by the user's `BasePath` (`IsSubPath`) and nearest `Meta` (hide regex / read_users / password, with `CanSeeHides`/`CanAccessWithoutPassword` permission bits on `User`). **Incremental updates are serialized per-parent** (mirrors Go `internal/search/update_lock.go`): a static `Dictionary<parent, UpdateLock>` of semaphore+refcount entries queues same-directory diffs while allowing different parents to run in parallel.
- ~~`/api/admin/scan/{stop,progress}` — only `/scan/{path}` (start) exists~~ **Implemented** — `POST /admin/scan/start` (body `{path, limit}`), `POST /admin/scan/stop`, `GET /admin/scan/progress` (returns `{obj_count, is_done}` matching frontend).

**Task routes**:
- ~~`/api/task/cancel_some`, `/delete_some`, `/retry_some` — batch operations~~ **Implemented** — TaskEndpoints refactored to frontend's `/api/task/{type}/...` path pattern. Batch ops take `string[]` body, return `{data: {tid: errorMsg}}` (empty = all succeeded). Added `ClearSucceededAsync`, `RetryFailedAsync`, `CancelSomeAsync`, `DeleteSomeAsync`, `RetrySomeAsync` to `ITaskService`/`TaskService`.
- ~~`/api/task/clear_succeeded`, `/retry_failed`~~ **Implemented** (see above).

**Misc public routes**:
- ~~`/i/:link_name` — Plist (iOS video streaming)~~ **Implemented** (`MiscEndpoints.MapMiscApi`; decodes base64 `[url]/[name@identifier]`, emits Apple plist XML for IPA install)
- ~~`/favicon.ico`, `/robots.txt`, `/manifest.json`~~ **Implemented** (`MiscEndpoints`): `/manifest.json` returns PWA manifest using `site_title` from settings; `/robots.txt` returns `User-agent: *\nAllow: /`; `/favicon.ico` 302-redirects to the `logo` setting (404 if unset).

**初始化向导**（Go v4.2.6 `feat!(init)` #3041，`server/handles/setup.go`）：
- ~~`GET|POST /api/public/init_status` — 系统是否已初始化（是否已存在管理员）~~ **Implemented** (`PublicEndpoints`) → `Resp<{initialized: bool}>`；判定复用了新增的 `IUserService.GetAdminAsync`（无管理员时 404，镜像 Go `op.GetAdmin` / `gorm.ErrRecordNotFound`）。
- ~~`POST /api/public/init/setup` — 创建首个管理员 + 设置站点名~~ **Implemented**：body `{username, password, site_title}`；先校验必填，再拒绝"已初始化"（400 `system has already been initialized`），密码 `< 4` 返回 400，随后以 `Role=Admin / BasePath=/ / Permission=0x71FF` 建号并写入 `site_title` 设置项。前端安装向导据此实现。

### Driver extension interfaces (defined in `IFileSystemDriverExtensions.cs`)

- ~~`IFileSystemArchiveReader` — `ListArchiveEntriesAsync`~~ **Implemented on `LocalDriver`** via `OpenList.Drivers.Archive.SharpCompressArchiveSupport` (zip/rar/7z/tar/gz, SharpCompress). Any Zio-backed driver can reuse the helper.
- ~~`IFileSystemArchiveDecompress` — `DecompressAsync`~~ **Implemented on `LocalDriver`** (same helper; ZipSlip-guarded).
- `IFileSystemDirectUploader` — `GetDirectUploadInfoAsync`; no implementation
- `IFileSystemWithDetails` — no implementation

Implemented extensions: `IFileSystemLinkProvider` (WebDAV, S3), `IFileSystemUploader` (WebDAV, S3, FTP, SFTP).

## API 对齐进展（2026-08-16，对照 Go main / 前端 v4.2.5）

对照 `docs/alignment-plan.md` 落地的 P0+P1+P2 项（不含 Cloud 驱动）：

- **Multipart 分片上传**（新增，前端破坏性缺口）：`/api/fs/multipart/{init,chunk,status,complete,abort}`。`IMultipartUploadService`/`MultipartUploadService`（会话内存 + 临时文件缓冲，窗口 8 分片流控 429、断点续传、failed_retriable）；`MultipartEndpoints`。POST 读 body，chunk 走 PUT。**设置门控**：`multipart_enabled`（默认 true）关闭时 `/init` 返回 403；`multipart_chunk_size`（MB，默认 10）作为客户端分片大小上界（`ResolveChunkSize`，1 MB 下限）；chunk 端点无论结果都**有界排空请求体**（镜像 Go MultipartChunk 的 io.LimitReader+io.Copy(io.Discard)），避免拒绝的分片污染客户端连接池。**会话 GC**（镜像 Go `internal/multipart` StartGC）：滑动 TTL 30 分钟不活动回收会话并清理临时目录，`Program.cs` 启动时清扫上次运行遗留的孤儿临时目录并启动周期回收。
- **消息 send 体形兼容**：`POST /admin/message/send` 现接受前端实际发送的 `{message:"string"}`（`SendBroadcastReq.Message` 改为 `object`，字符串包装为 `BroadcastMessage{Type="string"}`）。
- **归档驱动**：`OpenList.Drivers/Archive/SharpCompressArchiveSupport` + `LocalDriver` 实现 `IFileSystemArchiveReader/Decompress`（zip/rar/7z/tar/gz，ZipSlip 防护）→ `/fs/archive/*`、`/ad /ap /ae`、`/sad` 全部可用。
- **MCP 端点**：`/mcp`（Streamable HTTP + JSON-RPC 2.0，admin-only，`MCP.Enable` 开关，未启用 403）。`IMcpServerService`/`McpServerService`（会话 TTL 30min、128 全局/16 用户）+ `McpEndpoints`。工具：`openlist.fs.list/get/link`。
- **路由方法兼容（Go Any）**：`/fs/{list,get,dirs,search,other,archive/meta,archive/list}`、`/public/{settings,archive_extensions,offline_download_tools}`、`/share/list`、`/ping` 均接受 GET+POST（POST 读 body / GET 绑 query，`RequestBinder`）。
- **`/api/fs/regex_rename`**：`POST`，body `{src_dir, src_name_regex, new_name_regex}` → `FileSystemService.RegexRenameAsync`。
- **`/api/fs/link` 收紧为 admin-only**（对齐 Go `AuthAdmin`）。
- **S3 网关鉴权加固（P2）**：`S3Endpoints.ResolveUserAsync` 原在 JWT 中间件未捕获时把任何 token 原样接受为硬编码 Admin guest（安全洞）。现经 `IAuthService.ValidateTokenAsync` 校验 token → `IUserService.GetAsync` 加载真实用户，无效 token / 禁用用户一律返回 401。支持 SigV4 `Credential=<jwt>` / `AWS` / `Bearer` / 裸 token 四种头形态。
- **`/api/admin/task/*` 别名**：`TaskEndpoints.MapTaskRoutes` 供 `/api/task` 与 `/api/admin/task` 复用（旧自动化脚本兼容）。
- **`/debug/*`**：`DebugEndpoints`（`gc`/`hide_privacy`/`path/{**path}`），仅 Development 注册。
- **`set_guangyapan`**：`POST /admin/setting/set_guangyapan`（存 `guangyapan_temp_dir`）。

**前端关联**：`frontend/` 已修复为指向 `OpenListTeam/Openlist-Frontend`（v4.2.5, commit `feae2d0`）的 git submodule（原为损坏的 gitlink 160000 + 旧 v4.2.2 dist）。`pnpm build` 产出 `frontend/dist`，后端 `DistDir=../../frontend/dist` 天然匹配。`src/Example.Tests` 残留工程已删除。

## S3 网关增强（2026-09-21，对照 Go v4.2.6）

`S3Endpoints` 在基础对象读写之上补齐了 S3 客户端实际依赖的能力：

- **分片上传（Multipart Upload）**（Go `server/s3/multipart.go` #2813）：`POST /s3/{bucket}/{key}?uploads` 初始化；`PUT …?partNumber=N&uploadId=X` 上传分片；`POST …?uploadId=X` 完成（body 为 `CompleteMultipartUpload` XML）；`DELETE …?uploadId=X` 中止；`GET /s3/{bucket}?uploads` 列出进行中上传。新增 `S3MultipartStore`（singleton，`src/OpenList.Server/Services/S3MultipartStore.cs`）：分片流式落盘到 `%TEMP%/openlist-s3-multipart/{uploadId}/part-NNNNN`，支持乱序上传，Complete 时按 partNumber 升序用 `ConcatStream` 合并后走 `PutStreamAsync` 写入目标；校验 parts 升序、逐分片 ETag 匹配、`Content-Length` 不符则报 `IncompleteBody`；失败保留会话以便重试，成功/中止后清理临时目录。**TTL 回收**（镜像 Go `startReaper`）：默认 24h 空闲即回收，间隔 = TTL/4 夹在 [10s, 1h]，可由 `S3.MultipartTTL`（Go duration 字符串，如 `24h`/`30m`）配置；`Program.cs` 启动时清扫上次运行遗留的孤儿目录并启动周期回收。ETag 规则：part = `"md5hex"`，complete = `"hex(md5(concat(part md5 原始字节)))-N"`。
- **ListObjectsV2**：`GET /s3/{bucket}?list-type=2` 返回 `KeyCount` / `ContinuationToken` / `NextContinuationToken` / `StartAfter`，对象先按 key 稳定排序再截断，支持 `continuation-token` / `start-after` 续传（V1 同步补 `marker` / `NextMarker` 与截断）。
- **服务端复制 CopyObject**：`PUT /s3/{bucket}/{key}` 带 `x-amz-copy-source: /srcBucket/srcKey` 头 → 复制到目标并返回 `CopyObjectResult`。
- **批量删除 DeleteObjects**：`POST /s3/{bucket}?delete`（XML `Delete/Object/Key` 列表）→ `DeleteResult`（逐条 `Deleted` 或 `Error`）。
- **Range 请求**：`GET /s3/{bucket}/{key}` 支持 `Range`，返回 **206 + `Content-Range`**（对应 Go `server/s3/range.go`）；`HEAD` 返回 `Accept-Ranges: bytes`。
- **实现注意**：解析请求体 XML 必须**异步**读取（`StreamReader.ReadToEndAsync` → `XmlDocument.LoadXml`）——ASP.NET Core 默认 `AllowSynchronousIO=false`，直接 `XmlDocument.Load(Request.Body)` 会抛 `InvalidOperationException` 并导致 Complete/DeleteObjects 静默失败（返回 400 / 空结果）。

## Known Issues

- **WebDAV 集成测试挂起（基线遗留，非本次引入）**：`WebDavEndpointsTests.Delete_RemovesFile`（及同类的首个 WebDAV 请求）在测试进程内无限挂起，拖死整个 `dotnet test`。已用 `[Skip]` 临时跳过并注明原因；根因待查（疑似 WebDAV 首个请求在 Basic 认证或挂载层死锁）。真实服务器上的 `/dav` 写入路径需在修复后回归。
- Cloud 驱动（Phase 9，Go 侧 89 个驱动）**未实现** —— 仅完成公共基础设施 + 一个骨架驱动：
  - ✅ 地基（已实现并有单测）：`IStorageAdditionSink`（token 回写）、`RequestRateLimiter`（每驱动限流）、`OAuthTokenRefresher`（OAuth 刷新）、`CloudDriverBase`（共享 HTTP + 三者串联）。
  - ⚠️ **`AliyundriveOpen`（`src/OpenList.Drivers/Cloud/AliyundriveOpen/`）是骨架，未经真实服务验证，不可用于生产**：14 个配置字段已逐项对齐 Go `drivers/aliyundrive_open/meta.go`（前端表单可正常渲染），但 `InitAsync` 故意不设置 `System`（因此挂载点为空、不注册挂载），`RefreshTokenOnceAsync` / `GetLinkAsync` / `PutAsync` / `PutFormAsync` 一律抛 `NotImplementedException`。配置页通过 `Alert` 显示 warning banner 提示未实现。
  - 待移植的其余云驱动（按 Go 体量由小到大可选）：`wopan`(294 行)、`mega`(387)、`weiyun`(507)、`teambition`(577)、`dropbox`(607)、`google_drive`(730)、`onedrive`(739)、`quark_open`(894)、`quark_uc`(941)、`aliyundrive_open`(1065)、`123_open`(1156)、`baidu_netdisk`(1266)、`189`(1339)、`lanzou`(1465)、`thunder`(1509)、`guangyapan`(1666)。
  - 移植参考实现顺序建议：先补齐 `AliyundriveOpen` 的 token 刷新与列目录，再照此模板扩展。
