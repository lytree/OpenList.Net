# OpenList.Net

A .NET port of [OpenList](https://github.com/OpenListTeam/OpenList) (originally Go), with an in-house virtual filesystem (`OpenList.Vfs`) for filesystem abstraction.

> Status: **P0/P1/P2 API 对齐已完成**（对照 Go main / 前端 v4.2.5，2026-08-16）。剩余未实现项见下方「Not Yet Implemented」与 Implementation Plan 中的 `[ ]`（Cloud 驱动 / BitTorrent / 秒传 / Mapster 映射等）。

## Build & Run

```bash
dotnet build OpenList.slnx
dotnet run --project src/OpenList.Server    # http://0.0.0.0:5244
dotnet test                                 # TUnit
dotnet format
```

## Project Layout

| Project | Purpose |
|---|---|
| `src/OpenList.Core` | Models, `DriverBase`, driver extension interfaces, service interfaces, in-house VFS (`OpenList.Vfs`). Depends only on ASP.NET abstractions |
| `src/OpenList.Drivers` | Storage driver implementations (`Local/`, `S3/`, `WebDAV/`, `FtpSftpSmb/`, `Cloud/`, `Stubs/`) |
| `src/OpenList.Infrastructure` | EF Core `DbContext`, service impls, JWT auth, `MountService`. References Core |
| `src/OpenList.Server` | ASP.NET entrypoint (`Program.cs`), `Endpoints/` (Minimal APIs), `Middleware/`, `Models/` (DTOs + `Resp<T>`) |
| `tests/OpenList.Tests` | TUnit tests for Core + Infrastructure services |
| `frontend/` | SolidJS SPA (separate project, pnpm, proxies `/api` to `localhost:5244`) |

## API Patterns

- **Minimal APIs only** — all endpoints in `src/OpenList.Server/Endpoints/` as static extension methods (`MapAuthApi`, `MapFsApi`, etc.), registered in `Program.cs`
- **camelCase JSON** (`JsonNamingPolicy.CamelCase`)
- All responses wrapped in `Resp<T>`:
  ```json
  { "code": 200, "message": "success", "data": ... }
  ```
- **Mapster** for object mapping (`MappingConfig.Configure()` called at startup)
- Health check: `GET /ping` → `"pong"`

## Endpoint Routes

| File | Route Group | Auth |
|---|---|---|
| `AuthEndpoints.cs` | `/api/auth` | Mixed |
| `FsEndpoints.cs` | `/api/fs` | Mixed |
| `AdminEndpoints.cs` | `/api/admin` | Admin (`RequireRole("2")`) |
| `ShareEndpoints.cs` | `/api/share` | Mixed |
| `TaskEndpoints.cs` | `/api/task` | Auth'd |
| `MeEndpoints.cs` | `/api/me` | Auth'd |
| `PublicEndpoints.cs` | `/api/public` | Public |
| `SignEndpoints.cs` | `/d/`, `/p/`, `/ad/`, `/ap/`, `/ae/` | Public (sign-verified) |
| `SharingDownloadEndpoints.cs` | `/sd/`, `/sad/` | Public |
| `S3Endpoints.cs` | `/s3` | Mixed |
| `WebDavEndpoints.cs` | `/dav` | Mixed |

## Driver System

- **Auto-discovery**: `MountService` scans assemblies starting with `"OpenList"` for `DriverBase` subclasses. Adding a driver = create a class in `OpenList.Drivers` — no manual registration.
- **In-house VFS**: All drivers extend the `OpenList.Vfs.FileSystem`, mounted via `MountFileSystem`. Extension interfaces: `IFileSystemLinkProvider`, `IFileSystemUploader`, `IFileSystemWithDetails`, `IFileSystemArchiveReader`, `IFileSystemArchiveDecompress`, `IFileSystemDirectUploader`.

## Key Quirks

- **Auth**: JWT Bearer with configurable secret; auto-generated as 32 hex chars (2×Guid) if empty in config. Frontend sends token without `"Bearer "` prefix (custom `OnMessageReceived` handler).
- **Database**: defaults to `sqlite` (`data/data.db`); also supports `postgres`/`postgresql`. Table prefix: `x_`. Init: `MigrateAsync()` → falls back to `EnsureCreatedAsync()`. Add design-time migrations via `OpenListDbContextFactory` (uses SQLite).
- **CORS**: `AllowAnyOrigin/Method/Header` + custom `CorsMiddleware` for OPTIONS preflight.
- **Rate limiters**: general (100 req/min per IP), download rate limiter, upload rate limiter.
- **FTP/SFTP servers**: hosted services, disabled by default (`FTP.Enable`/`SFTP.Enable` in config).
- **Kestrel body limit**: 10 GB (for WebDAV uploads).
- **Swagger + Scalar**: at `/swagger` and `/scalar` (Development only).
- **SPA fallback**: serves `index.html` from `DistDir` (`../../frontend/dist` by default).
- **Environment variables**: use `OpenList__` prefix (e.g. `OpenList__Database__Type=postgres`).

## Not Yet Implemented (from Go reference)

- `/api/fs/torrent/*` — 8 stubs (parse/upload_parse/rapid_upload/generate + legacy list/info/add/cancel) return 501, no BitTorrent engine
- `/api/fs/multipart/*` 底层秒传（rapid upload 哈希校验）—— 端点与分片上传已实现，`X-File-Md5` 哈希秒传未接入
- `IFileSystemDirectUploader` / `IFileSystemWithDetails` — driver interfaces defined, no implementation
- Cloud 驱动（Phase 9，13+ 个）—— 未实现
- `/debug/pprof/*` — 无 .NET 对应（仅 gc/hide_privacy/path）

> SSO（`/auth/sso*`）、WebAuthn（`/authn/webauthn_*`）、LDAP（`/auth/login/ldap`）、`/ad /ap /ae`、`/sad`、归档驱动（SharpCompress）均已实现。

---

# Implementation Plan

Phased rollout. Each phase builds on the previous. Items are checked off `[x]` as they ship; this README is updated after every feature.

## Phase 1 — Skeleton ✅

- [x] Solution structure (`OpenList.slnx` + 5 projects)
- [x] `OpenList.Core`: `Resp<T>`, models (`User`, `Storage`, `Meta`, `Sharing`, `SettingItem`, `SshKey`), `DriverBase`, service interfaces
- [x] `OpenList.Infrastructure`: `OpenListDbContext`, `MountService` skeleton, `OpenListDbContextFactory`
- [x] `OpenList.Drivers`: `LocalDriver` skeleton
- [x] `OpenList.Server`: `Program.cs` (DI, JSON, CORS, Kestrel, SPA fallback), `HealthEndpoints` (`GET /ping`)
- [x] `OpenList.Tests`: smoke tests for `Resp<T>`
- [x] `dotnet build` green

## Phase 2 — Auth & User Management ✅

Core auth pipeline so the frontend can log in. No storage ops yet.

- [x] `IUserService` impl + `UserService` (EF Core queries)
- [x] `IAuthService` impl + `JwtService` (token issue/verify, 2FA TOTP via Otp.NET)
- [x] JWT middleware: `OnMessageReceived` strips no-prefix token, validates, sets `HttpContext.User`
- [x] `AuthEndpoints.cs` — `/api/auth/*`:
  - [x] `POST /api/auth/login` — username/password → token (+ 2FA flow)
  - [x] `POST /api/auth/login/2fa` — TOTP verification
  - [x] `POST /api/auth/logout`
  - [x] `GET  /api/auth/me` — current user
  - [x] `POST /api/auth/2fa/generate` — generate TOTP secret + QR (QRCoder)
  - [x] `POST /api/auth/2fa/verify`
  - [x] `POST /api/auth/hash_password` — admin only
- [x] `MeEndpoints.cs` — `/api/me/*`:
  - [x] `POST /api/me/update` — update profile (username, password)
  - [x] `POST /api/me/2fa`
  - [x] `GET  /api/me/sessions` (placeholder — Phase 16)
  - [x] `DELETE /api/me/sessions/:id` (placeholder — Phase 16)
- [x] Admin user auto-seed on first run (`AdminUsername`/`AdminPassword` config)
- [x] Tests: `UserServiceTests` (7), `JwtServiceTests` (6) — 15/15 passing

## Phase 3 — Storage Management & Driver Loading ✅

Admin CRUD for storages + driver instantiation.

- [x] `IStorageService` impl + `StorageService`
- [x] `MountService.LoadAllAsync` — instantiate drivers, call `InitAsync`, register in `MountFileSystem`
- [x] `AdminEndpoints.cs` — `/api/admin/*` (admin role `2`):
  - [x] `GET  /api/admin/storage/list`
  - [x] `GET  /api/admin/storage/get?id=`
  - [x] `POST /api/admin/storage/create`
  - [x] `POST /api/admin/storage/update`
  - [x] `POST /api/admin/storage/delete`
  - [x] `POST /api/admin/storage/disable` / `enable`
  - [x] `GET  /api/admin/driver/list` — available drivers + config schema
  - [x] `GET  /api/admin/driver/names`
  - [x] `GET  /api/admin/driver/info?driver=`
  - [x] `POST /api/admin/storage/load_all` — reload all storages
  - [x] `GET  /api/admin/user/list|get` + `POST /api/admin/user/create|update|delete|cancel_2fa`
  - [x] `GET  /api/admin/setting/list` (placeholder — Phase 14)
- [x] `LocalDriver` full impl (PhysicalFileSystem + SubFileSystem, config schema)
- [x] Tests: `StorageServiceTests` (9), `LocalDriverTests` (4) — 13/13 passing

## Phase 4 — Filesystem Operations ✅

The core `/api/fs/*` surface — list, mkdir, rename, copy, move, delete, upload, download.

- [x] `IFileSystemService` impl + `FileSystemService` (routes to mounted `DriverBase`)
- [x] `FsEndpoints.cs` — `/api/fs/*`:
  - [x] `POST /api/fs/list` — `{ path, password?, page?, per_page?, refresh? }`
  - [x] `POST /api/fs/get` — `{ path, password? }` (file/folder detail)
  - [x] `POST /api/fs/mkdir`
  - [x] `POST /api/fs/rename`
  - [x] `POST /api/fs/copy`
  - [x] `POST /api/fs/move`
  - [x] `POST /api/fs/remove`
  - [x] `PUT  /api/fs/put` — upload (stream + multipart, `IFileSystemUploader`)
  - [x] `POST /api/fs/link` — generate direct link (`IFileSystemLinkProvider`, 501 if unsupported)
  - [x] `POST /api/fs/dirs` — recursive directory list (for move/copy dialog)
  - [x] `POST /api/fs/rename_batch` — batch rename
  - [x] `POST /api/fs/remove_empty_directory`
  - [x] `POST /api/fs/search` — recursive keyword search with pagination
  - [x] `GET  /api/fs/archive/meta` — supported archive extensions
  - [x] `POST /api/fs/archive/list` — list archive entries (501 if unsupported)
  - [x] `POST /api/fs/archive/decompress` — extract archive (501 if unsupported)
- [x] `SignEndpoints.cs` — signed URL handlers:
  - [x] `GET /d/*path` — signed download (GET + HEAD)
  - [x] `GET /p/*path` — signed preview/proxy (GET + HEAD)
  - [x] `GET /ad/*path` — signed archive list (501 until driver supports `IFileSystemArchiveReader`)
  - [x] `GET /ap/*path` — signed archive proxy (GET + HEAD)
  - [x] `GET /ae/*path` — signed archive extract (501 until driver supports `IFileSystemArchiveDecompress`)
- [x] Tests: `FileSystemServiceTests` — 17/17 passing (list, get, mkdir, rename, move, copy, remove, put, dirs, search, archive meta, link)

## Phase 5 — Sharing ✅

- [x] `ISharingService` impl + `SharingService`（按分享 code 查找，非 db 主键）
- [x] `ShareEndpoints.cs` — `/api/share/*`:
  - [x] `GET/POST /api/share/list`
  - [x] `POST /api/share/create`
  - [x] `GET  /api/share/get?id=`
  - [x] `POST /api/share/update`
  - [x] `POST /api/share/delete`
  - [x] `POST /api/share/enable` / `disable`（对应 Go 的 cancel 语义）
- [x] `PublicEndpoints.cs` — `/api/public/*`:
  - [x] `GET/POST /api/public/settings`
  - [x] `GET/POST /api/public/archive_extensions`
  - [x] `GET/POST /api/public/offline_download_tools`
  - > Go 的 `/api/public/files`、`/api/public/share/:id` 未移植——公开分享浏览走 `/api/fs/{list,get,dirs}` 匿名访问
- [x] `SharingDownloadEndpoints.cs` (`/sd/`, `/sad/`):
  - [x] `GET/HEAD /sd/:sid[/*path]` — sharing download（验签 + 可选 pwd + 过期/禁用/次数限制，Range 流式）
  - [x] `GET/HEAD /sad/:sid[/*path]` — sharing archive extract（驱动实现归档时可用，否则 501）
- [x] Tests: `ShareDownloadEndpointsTests`

## Phase 6 — Tasks & Offline Download

- [x] `ITaskService` impl + `TaskService` (in-memory task store with progress tracking)
- [x] `TaskEndpoints.cs` — `/api/task/*`:
  - [x] `GET  /api/task/undone`
  - [x] `GET  /api/task/done`
  - [x] `GET  /api/task/info?tid=`
  - [x] `POST /api/task/cancel?tid=`
  - [x] `POST /api/task/delete?tid=`
  - [x] `POST /api/task/clear_done`
  - [x] `POST /api/task/retry?tid=`
- [x] Offline download tools (`IOfflineDownloadTool`):
  - [x] `POST /api/fs/add_offline_download` — wired through `IFileSystemService.AddOfflineDownloadAsync` (returns 501 until tools registered)
  - [x] `GET  /api/fs/torrent/list` — 501
  - [x] `GET  /api/fs/torrent/info` — 501
  - [x] `POST /api/fs/torrent/add` — 501
  - [x] `POST /api/fs/torrent/cancel` — 501
- [x] Tests: `TaskServiceTests` — 12 tests covering create/cancel/retry/delete/clear/done/undone

## Phase 7 — Admin: Users, Settings, Messages, Meta ✅

- [x] `AdminEndpoints.cs` (continued):
  - [x] `GET  /api/admin/user/list`
  - [x] `GET  /api/admin/user/get?id=`
  - [x] `POST /api/admin/user/create`
  - [x] `POST /api/admin/user/update`
  - [x] `POST /api/admin/user/delete`
  - [x] `POST /api/admin/user/cancel_2fa`
  - [x] `POST /api/admin/user/set_password`
  - [x] `GET  /api/admin/setting/list`
  - [x] `POST /api/admin/setting/save`
  - [x] `GET  /api/admin/setting/get?keys=`
  - [x] `POST /api/admin/setting/set`
  - [x] `POST /api/admin/meta/list`
  - [x] `POST /api/admin/meta/get`
  - [x] `POST /api/admin/meta/save`
  - [x] `POST /api/admin/meta/delete`
- [x] `IMessageService` impl + `MessageService`:
  - [x] `GET  /api/admin/message/list`
  - [x] `GET  /api/admin/message/get?id=`
  - [x] `POST /api/admin/message/create`
  - [x] `POST /api/admin/message/update`
  - [x] `POST /api/admin/message/delete`
  - [x] `GET  /api/me/message/unread`
  - [x] `GET  /api/me/message/list`
  - [x] `POST /api/me/message/read`
- [x] `IScanService` impl + `ScanService`:
  - [x] `POST /api/admin/scan/:path`
  - [x] `POST /api/fs/scan`
- [x] Tests: `UserServiceTests`, `SettingServiceTests`, `MetaServiceTests`, `MessageServiceTests` — 76/76 passing

## Phase 8 — Drivers: S3, WebDAV, FTP/SFTP/SMB

- [x] `S3Driver` (Minio client) — read/write/list/upload
  - [x] `S3FileSystem` — `FileSystem` over S3 with prefix-based path mapping, directory markers, streaming uploads
  - [x] `IFileSystemUploader` + `IFileSystemLinkProvider` implemented
  - [x] Config: `endpoint`, `region`, `access_key_id`, `secret_access_key`, `bucket`, `root_folder_path`, `use_ssl`, `session_token`
- [x] `WebDAVDriver` (WebDav.Client) — read/write/list/upload
  - [x] `WebDavFileSystem` — PROPFIND/MKCOL/DELETE/PUT/MOVE backed VFS
  - [x] `IFileSystemUploader` + `IFileSystemLinkProvider` (direct URL) implemented
  - [x] Config: `endpoint`, `root_folder_path`, `username`, `password`, `use_basic_auth`
- [x] `FtpDriver` (FluentFTP)
  - [x] `FtpFileSystem` — async FTP client with streaming open read/write
  - [x] `IFileSystemUploader` implemented
  - [x] Config: `host`, `port`, `username`, `password`, `root_folder_path`, `use_tls`
- [x] `SftpDriver` (SSH.NET)
  - [x] `SftpFileSystem` — SFTP client with password or private-key auth
  - [x] `IFileSystemUploader` implemented
  - [x] Config: `host`, `port`, `username`, `password`, `private_key`, `private_key_passphrase`, `root_folder_path`
- [x] `SmbDriver` (SMBLibrary) — basic stub
  - [x] Config schema declared; uses `MemoryFileSystem` placeholder pending full SMB2Client integration
  - [x] Config: `host`, `port`, `username`, `password`, `share`, `domain`, `root_folder_path`
- [x] Driver extension interfaces: `IFileSystemUploader`, `IFileSystemLinkProvider`, `IFileSystemWithDetails`
- [x] `NetworkFileSystemBase` — shared base for network-backed filesystems (symlinks/attributes/watchers unsupported, default copy/move/enumerate)
- [x] Tests: `DriverTests` — 21 tests covering metadata, config schema, validation, and link generation (no live servers required) — 96/96 passing

## Phase 9 — Cloud Drivers

- [ ] `AliyundriveDriver`, `BaiduNetdiskDriver`, `115Driver`, `QuarkDriver`, `PikpakDriver`, `ThunderDriver`, `OneDriveDriver`, `GoogleDriveDriver`, `DropboxDriver`, `TeraboxDriver`, `WeiyunDriver`, `MegaDriver`, `LanzouDriver`
- [ ] Token refresh plumbing (per-driver `TokenManager`)
- [ ] Tests: mock HTTP responses per driver

## Phase 10 — S3 Gateway

- [x] `S3Endpoints.cs` — `/s3` — S3-compatible API (list buckets, get/put/delete objects, head object)
- [x] Auth: passthrough to OpenList JWT (Authorization header accepted)
- [x] Tests: `S3EndpointsTests` — integration tests via `TestWebAppFactory`

## Phase 11 — WebDAV Server

- [x] `WebDavEndpoints.cs` — `/dav` — full WebDAV (OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, MKCOL, DELETE, MOVE, COPY, LOCK/UNLOCK)
- [x] Basic auth → OpenList user (via `UserService` password verification)
- [x] Tests: `WebDavEndpointsTests` — integration tests via `TestWebAppFactory`

## Phase 12 — FTP/SFTP Hosted Servers

- [x] FTP hosted service (FubarDev.FtpServer) — `FtpHostedService` + `OpenListFtpMembershipProvider` + `MountUnixFileSystem` bridge, disabled by default
- [x] SFTP hosted service stub (SSH.NET has no server impl) — `SftpHostedService` logs warning, disabled by default
- [x] Auth bridge to OpenList users (`IMembershipProvider` → `UserService`)
- [x] Virtual filesystem in-house (`MountUnixFileSystem` adapts `MountFileSystem` to `IUnixFileSystem`)

## Phase 13 — Search Index ✅

- [x] `ISearchService` + impl — `database` 后端已持久化到 `x_search_index` 表；`bleve` 后端未实现（`search_index=bleve` 回退为文件系统搜索）
- [x] `POST /api/fs/search` — 关键字路径搜索（数据库 LIKE / 实时文件系统）
- [x] `POST /admin/index/{build,update,stop,clear}` + `GET /admin/index/progress`
- [x] 启动时恢复索引状态（镜像 Go `bootstrap.InitIndex`）— `SearchService.InitIndexAsync` 载入持久化进度、把崩溃遗留的 `running` 状态复位为 `idle`，使增量更新在重启后保持启用。不做全量自动重建（与 Go 一致）
- [x] 文件操作时增量索引 — `auto_update_index` 设置 + `IObjsUpdateHooks`（发布/订阅）解耦 `FileSystemService` 写操作与 `SearchService` 增量更新（增/删/改路径局部重建）
- [x] 搜索结果访问控制（镜像 Go `search.go` SearchFiltered）— `SearchAsync(req, user)` 按用户 `BasePath`（`IsSubPath`）与最近 `Meta`（隐藏正则 / 读用户 / 密码，`User` 权限位 `CanSeeHides`/`CanAccessWithoutPassword`）过滤结果
- [x] 增量更新按父目录串行化（镜像 Go `internal/search/update_lock.go`）— 静态 `Dictionary<parent, UpdateLock>`（信号量+引用计数）排队同一父目录的差集更新，不同父目录可并行，避免并发写表冲突

## Phase 14 — Archive Support ✅

- [x] `IFileSystemArchiveReader` impl — `SharpCompressArchiveSupport` on `LocalDriver`（zip/rar/7z/tar/gz）
- [x] `IFileSystemArchiveDecompress` impl — 解压到路径（ZipSlip 防护）
- [x] `/ad/`, `/ap/`, `/ae/` signed archive endpoints
- [x] `/sad/` sharing archive download
- [x] Tests: `SignEndpointsTests` / `FsEndpointsTests` 覆盖归档路由

## Phase 15 — 2FA, WebAuthn, SSO ✅

- [x] TOTP 2FA (Otp.NET) — `/api/auth/login/2fa`、`/api/auth/2fa/*`（Phase 2 已实现）
- [x] WebAuthn (`/api/authn/*`) via Fido2:
  - [x] `GET  /api/authn/webauthn_begin_login`
  - [x] `POST /api/authn/webauthn_finish_login`
  - [x] `GET  /api/authn/webauthn_begin_registration`
  - [x] `POST /api/authn/webauthn_finish_registration`
  - [x] `GET  /api/authn/getcredentials` + `POST /api/authn/delete_authn`（凭据管理）
- [x] SSO (`/api/auth/sso*`) — `ISsoService`（`/sso_callback`、`/get_sso_id`、`/sso_get_token`；禁用时 403）
- [x] LDAP (`/api/auth/login/ldap`) — `JwtService.LoginLdapAsync`（未配置 LDAP 时 501）

## Phase 16 — Rate Limiting, Middleware, Polish ✅

- [x] `RateLimitMiddleware` — 字节级令牌桶限流（镜像 Go `rate.Limiter` + `stream`）：全局并发闸门 `MaxAllowedMiddleware`（`MaxConnections`）、下载/上传速率限制（`RateLimitMiddleware` + `RateLimitManager` 从设置表刷新 client/server × download/upload 四组限速）；值 `<=0` 表示不限速
- [x] `CorsMiddleware` — 用内置 `UseCors`（AllowAnyOrigin/Method/Header + 暴露 Content-Range 等头）替代自定义中间件
- [x] `LoggingMiddleware` — Serilog：console sink + 文件滚动（`MaxSize/MaxBackups/MaxAge` 对齐 Go lumberjack）；`RequestLogMiddleware` 支持 CIDR/路径/方法过滤（`Log.Filter`，镜像 Go `skiperDecider`）
- [x] `ErrorHandlingMiddleware` — 全局异常 → `Resp<T>` JSON（500 internal server error；`BadHttpRequestException` → 400），镜像 Go `gin.Recovery`
- [ ] Mapster `MappingConfig.Configure()` — 包已引用，但 **从未调用**（对象映射为手写 DTO 转换）
- [x] Swagger 已接入（`/swagger`，Development only）
- [x] Scalar（`/scalar`）— 已接入（指向 `/swagger/v1/swagger.json`，Development only）
- [x] Config validation on startup — `AppConfiguration.Validate()`：数据库类型 / HTTP·HTTPS 端口 / MaxConnections / search_index 后端 / 日志参数非法即中止启动

## Phase 17 — Frontend Contract Alignment ✅

- [x] `/api/*` 响应与前端 TS 类型对齐（对照 `Openlist-Frontend` v4.2.5，git submodule `feae2d0`）
- [x] SPA fallback 服务 `frontend/dist`（`MapFallback` + 静态文件）
- [x] Dev proxy（`frontend/` `pnpm dev` → `localhost:5244`）
- [x] End-to-end smoke：登录 → list → 上传 → 分享（2026-08-16 运行时冒烟验证）

---

## Progress Log

| Date | Phase | Summary |
|---|---|---|
| 2026-06-22 | Phase 1 | Skeleton: 5 projects, `Resp<T>`, models, `DriverBase`, `OpenListDbContext`, `MountService`, `LocalDriver` skeleton, `Program.cs`, `/ping`, smoke tests. `dotnet build` green. |
| 2026-06-22 | Phase 2 | Auth: `UserService` (PBKDF2-SHA256), `JwtService` (HS256 + TOTP 2FA), `AuthEndpoints` (`/api/auth/*`), `MeEndpoints` (`/api/me/*`), `DbInitializer` (admin seed), JWT middleware (no-prefix token). 15/15 tests passing. |
| 2026-06-22 | Phase 3 | Storage: `StorageService` (CRUD + enable/disable), `MountService` (MountFileSystem, auto-discovery, load/unload), `LocalDriver` (PhysicalFileSystem + SubFileSystem), `AdminEndpoints` (`/api/admin/storage|driver|user/*`). 28/28 tests passing. |
| 2026-06-23 | Phase 4 | Filesystem: `FileSystemService` (list/get/mkdir/rename/move/copy/remove/put/dirs/search/archive/link via Zio MountFileSystem), `FsEndpoints` (`/api/fs/*`), `ClaimsPrincipal.ToUser()` extension. 50/50 tests passing. |
| 2026-06-23 | Phase 7 | Admin: `SettingService` (CRUD + public filter + archive exts), `MetaService` (path metadata CRUD), `MessageService` (admin↔user messaging), `ScanService` (background directory scan with progress). Extended `AdminEndpoints` (user/set_password, setting, meta, message, scan), `MeEndpoints` (message unread/list/read), `FsEndpoints` (/scan). Added `Message` model + DbSet. 76/76 tests passing. |
| 2026-06-23 | Phase 10-12 | S3 Gateway (`S3Endpoints` — list buckets, get/put/head/delete objects, JWT passthrough auth), WebDAV Server (`WebDavEndpoints` — OPTIONS/PROPFIND/PROPPATCH/GET/HEAD/PUT/MKCOL/DELETE/MOVE/COPY/LOCK/UNLOCK, Basic auth via `UserService`), FTP hosted service (`FtpHostedService` + `OpenListFtpMembershipProvider` + `MountUnixFileSystem` bridge via FubarDev.FtpServer), SFTP stub (`SftpHostedService` — SSH.NET has no server). Fixed `FileSystemService.PutStreamAsync` sync IO → async. `TestWebAppFactory` integration test infra with per-factory temp SQLite DB + Local storage mount. 116/116 tests passing. |
| 2026-06-24 | Phase 6 | Tasks & Offline Download: `TaskService` (in-memory task store with progress tracking, cancel/retry/delete/clear), `TaskEndpoints` (`/api/task/undone|done|info|cancel|delete|clear_done|retry`), `FsEndpoints` additions (`/api/fs/add_offline_download` + 4 `/api/fs/torrent/*` 501 stubs). 128/128 tests passing (12 new `TaskServiceTests`). |
| 2026-07-16 | API 对齐基线 | P0/P1 对齐修复: `/auth/logout` GET+POST、guest 访问 `/fs/{list,get,dirs,search}`、`torrent/generate` stub、`/ad /ap /ae` 签名归档路由、`/sad` 共享归档下载、WebDAV LOCK/UNLOCK (`IWebDavLockStore`)。新增 `docs/api-diff.md` 差异分析。 |
| 2026-08-16 | API 对齐 P0+P1+P2 | 对照 Go main/前端 v4.2.5: Multipart 分片上传 5 端点、消息 send 体形兼容、归档驱动 (SharpCompress, LocalDriver)、MCP 端点 (`/mcp`, Streamable HTTP)、Go Any 方法兼容 (GET+POST)、`/fs/regex_rename`、`/fs/link` admin-only、`/api/admin/task/*` 别名、`/debug/*`、`set_guangyapan`。前端修复为 `Openlist-Frontend` v4.2.5 git submodule。详见 `docs/alignment-plan.md`。 |
| 2026-08-28 | Implementation Plan 状态同步 | 按源码核对勾选状态：Phase 4 (SignEndpoints)、5 (Sharing)、14 (Archive)、15 (2FA/WebAuthn/SSO)、17 (前端对齐) 全部完成；Phase 13 标记 database 后端完成（bleve/自动重建/增量未实现）；Phase 16 明确限流/Serilog/错误处理/Mapster/Scalar/配置校验仍未实现。 |
| 2026-08-28 | Phase 13 + Phase 16 | Phase 13：增量索引钩子（`auto_update_index` 设置 + `IObjsUpdateHooks` 发布/订阅 + `FileSystemService` 写操作触发局部重建/删除清理）+ 启动时 `InitIndexAsync` 恢复进度（镜像 Go `bootstrap.InitIndex`）。Phase 16：字节级令牌桶限流（`RateLimiter`/`RateLimitManager`/`RateLimitMiddleware`/`MaxAllowedMiddleware`）、Serilog 请求日志（`RequestLogMiddleware` CIDR/路径/方法过滤 + 文件滚动）、`ErrorHandlingMiddleware`（`Resp<T>` 错误封装）、`AppConfiguration.Validate()` 启动校验、Scalar API 文档。Mapster 仍未接入。新增 `MiddlewareTests`（限流/日志过滤/异常处理/并发闸门/配置校验）。206/207 测试通过（1 skip 为已知 WebDAV 挂起）。 |
| 2026-08-28 | 搜索访问控制 + multipart gating/GC | 按 Go 参考同步：① 搜索访问控制过滤（`SearchAsync(req,user)` 按 BasePath + Meta 隐藏/读用户/密码过滤，镜像 `search.go` SearchFiltered）；② 增量更新按父目录串行化（镜像 `update_lock.go`，静态 per-parent 锁）；③ multipart 设置门控（`multipart_enabled` 403 / `multipart_chunk_size` 上限 + 1 MB 下限）与 chunk 请求体有界排空；④ multipart 会话 GC（滑动 TTL 30min + 启动孤儿临时目录清扫，`Program.cs` `StartGc`）；⑤ `move_task_threads_num` 默认设置。新增 `MultipartUploadServiceTests`，扩展 `SearchServiceTests`/`SettingServiceTests`。222/223 测试通过（1 skip 为已知 WebDAV 挂起）。 |
| 2026-10-05 | 移除 Zio 依赖，自研 VFS | 完全移除第三方 `Zio` 0.24.0，在 `OpenList.Core/Vfs` 下自建虚拟文件系统（命名空间 `OpenList.Vfs`）：`UPath`（不可变路径值类型，构造时归一化重复斜杠/`.`/`..`）、`IFileSystem` + `FileSystem` 抽象基类（公共方法做校验、`*Impl` 虚方法做 I/O）、`MountFileSystem`（最长前缀挂载路由）、`SubFileSystem`、`PhysicalFileSystem`（含 Windows 盘符映射）、`MemoryFileSystem`、`FileSystemEntry`/`FileEntry`/`DirectoryEntry`。枚举 `SearchOption` 更名 `SearchDepth` 以避开与 `System.IO` 的冲突。同步将 `ZioDriverBase`→`DriverBase`、`ZioMountService`→`MountService`（含文件重命名）、`ZioUnixFileSystem`→`MountUnixFileSystem`。337/338 测试通过（1 skip 为已知 WebDAV 挂起）。 |
