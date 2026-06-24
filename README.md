# OpenList.Net

A .NET port of [OpenList](https://github.com/OpenListTeam/OpenList) (originally Go), built on [Zio](https://github.com/xoofx/zio) for filesystem abstraction.

> Status: **Phase 1 — skeleton**. APIs listed below are the implementation plan; each phase will check items off as they ship.

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
| `src/OpenList.Core` | Models, `ZioDriverBase`, driver extension interfaces, service interfaces. Depends only on Zio + ASP.NET abstractions |
| `src/OpenList.Drivers` | Storage driver implementations (`Local/`, `S3/`, `WebDAV/`, `FtpSftpSmb/`, `Cloud/`, `Stubs/`) |
| `src/OpenList.Infrastructure` | EF Core `DbContext`, service impls, JWT auth, `ZioMountService`. References Core |
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

- **Auto-discovery**: `ZioMountService` scans assemblies starting with `"OpenList"` for `ZioDriverBase` subclasses. Adding a driver = create a class in `OpenList.Drivers` — no manual registration.
- **Zio-based**: All drivers wrap a Zio `IFileSystem`, mounted via `MountFileSystem`. Extension interfaces: `IFileSystemLinkProvider`, `IFileSystemUploader`, `IFileSystemWithDetails`, `IFileSystemArchiveReader`, `IFileSystemArchiveDecompress`, `IFileSystemDirectUploader`.

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

- `/api/fs/torrent/*` — 4 endpoints return 501
- `/api/auth/sso/*` — SSO login
- `/api/authn/*` — WebAuthn
- `/ap/*path`, `/ae/*path` — signed archive proxy/extract (return 501)
- `/sad/*` — sharing archive download (returns 404)
- `IFileSystemArchiveDecompress` — interface defined, no implementation
- `IFileSystemDirectUploader` — interface defined, stubbed

---

# Implementation Plan

Phased rollout. Each phase builds on the previous. Items are checked off `[x]` as they ship; this README is updated after every feature.

## Phase 1 — Skeleton ✅

- [x] Solution structure (`OpenList.slnx` + 5 projects)
- [x] `OpenList.Core`: `Resp<T>`, models (`User`, `Storage`, `Meta`, `Sharing`, `SettingItem`, `SshKey`), `ZioDriverBase`, service interfaces
- [x] `OpenList.Infrastructure`: `OpenListDbContext`, `ZioMountService` skeleton, `OpenListDbContextFactory`
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
- [x] `ZioMountService.LoadAllAsync` — instantiate drivers, call `InitAsync`, register in `MountFileSystem`
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

- [x] `IFileSystemService` impl + `FileSystemService` (routes to mounted `ZioDriverBase`)
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
- [ ] `SignEndpoints.cs` — signed URL handlers (Phase 5+):
  - [ ] `GET /d/*path` — signed download
  - [ ] `GET /p/*path` — signed preview/proxy
  - [ ] `GET /ad/*path` — signed archive download (501 for now)
  - [ ] `GET /ap/*path` — signed archive proxy (501 for now)
  - [ ] `GET /ae/*path` — signed archive extract (501 for now)
- [x] Tests: `FileSystemServiceTests` — 17/17 passing (list, get, mkdir, rename, move, copy, remove, put, dirs, search, archive meta, link)

## Phase 5 — Sharing

- [ ] `IShareService` impl + `ShareService`
- [ ] `ShareEndpoints.cs` — `/api/share/*`:
  - [ ] `POST /api/share/list`
  - [ ] `POST /api/share/create`
  - [ ] `POST /api/share/get`
  - [ ] `POST /api/share/update`
  - [ ] `POST /api/share/delete`
  - [ ] `POST /api/share/cancel`
- [ ] `PublicEndpoints.cs` — `/api/public/*`:
  - [ ] `GET  /api/public/settings`
  - [ ] `GET  /api/public/files?path=&password=&page=&per_page=`
  - [ ] `GET  /api/public/share/:id`
  - [ ] `POST /api/public/share/:id` — submit password
- [ ] `SharingDownloadEndpoints.cs`:
  - [ ] `GET /sd/*` — sharing download (signed)
  - [ ] `GET /sad/*` — sharing archive download (404 for now)
- [ ] Tests: `ShareServiceTests`

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
  - [x] `S3FileSystem` — Zio `FileSystem` over S3 with prefix-based path mapping, directory markers, streaming uploads
  - [x] `IFileSystemUploader` + `IFileSystemLinkProvider` implemented
  - [x] Config: `endpoint`, `region`, `access_key_id`, `secret_access_key`, `bucket`, `root_folder_path`, `use_ssl`, `session_token`
- [x] `WebDAVDriver` (WebDav.Client) — read/write/list/upload
  - [x] `WebDavFileSystem` — PROPFIND/MKCOL/DELETE/PUT/MOVE backed Zio filesystem
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
- [x] `NetworkFileSystemBase` — shared base for network-backed Zio filesystems (symlinks/attributes/watchers unsupported, default copy/move/enumerate)
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

- [x] FTP hosted service (FubarDev.FtpServer) — `FtpHostedService` + `OpenListFtpMembershipProvider` + `ZioUnixFileSystem` bridge, disabled by default
- [x] SFTP hosted service stub (SSH.NET has no server impl) — `SftpHostedService` logs warning, disabled by default
- [x] Auth bridge to OpenList users (`IMembershipProvider` → `UserService`)
- [x] Virtual filesystem via Zio (`ZioUnixFileSystem` adapts `MountFileSystem` to `IUnixFileSystem`)

## Phase 13 — Search Index

- [ ] `ISearchService` + impl (Bleve-equivalent for .NET: Lucene.NET or database FTS)
- [ ] `POST /api/fs/search` — full-text path search
- [ ] Index rebuild on storage load
- [ ] Incremental index on file ops

## Phase 14 — Archive Support

- [ ] `IFileSystemArchiveReader` impl (SharpCompress) — list/extract from zip/rar/7z/tar/gz
- [ ] `IFileSystemArchiveDecompress` impl — extract to path
- [ ] `/ad/`, `/ap/`, `/ae/` signed archive endpoints
- [ ] `/sad/` sharing archive download
- [ ] Tests: `ArchiveReaderTests`

## Phase 15 — 2FA, WebAuthn, SSO

- [ ] TOTP 2FA (Otp.NET) — already stubbed in Phase 2
- [ ] WebAuthn (`/api/authn/*`) via Fido2
  - [ ] `POST /api/authn/register/begin`
  - [ ] `POST /api/authn/register/finish`
  - [ ] `POST /api/authn/login/begin`
  - [ ] `POST /api/authn/login/finish`
- [ ] SSO (`/api/auth/sso/*`) — OIDC/SAML providers

## Phase 16 — Rate Limiting, Middleware, Polish

- [ ] `RateLimitMiddleware` — general (100/min), download, upload
- [ ] `CorsMiddleware` — OPTIONS preflight handler
- [ ] `LoggingMiddleware` — Serilog request logging
- [ ] `ErrorHandlingMiddleware` — uniform `Resp<T>` error envelope
- [ ] Mapster `MappingConfig.Configure()`
- [ ] Swagger + Scalar wired (Development only)
- [ ] Config validation on startup

## Phase 17 — Frontend Contract Alignment

- [ ] Verify all `/api/*` responses match frontend TypeScript types
- [ ] Verify SPA fallback serves `frontend/dist`
- [ ] Verify dev proxy (`pnpm dev` → `localhost:5244`)
- [ ] End-to-end smoke: login → list → upload → share

---

## Progress Log

| Date | Phase | Summary |
|---|---|---|
| 2026-06-22 | Phase 1 | Skeleton: 5 projects, `Resp<T>`, models, `ZioDriverBase`, `OpenListDbContext`, `ZioMountService`, `LocalDriver` skeleton, `Program.cs`, `/ping`, smoke tests. `dotnet build` green. |
| 2026-06-22 | Phase 2 | Auth: `UserService` (PBKDF2-SHA256), `JwtService` (HS256 + TOTP 2FA), `AuthEndpoints` (`/api/auth/*`), `MeEndpoints` (`/api/me/*`), `DbInitializer` (admin seed), JWT middleware (no-prefix token). 15/15 tests passing. |
| 2026-06-22 | Phase 3 | Storage: `StorageService` (CRUD + enable/disable), `ZioMountService` (MountFileSystem, auto-discovery, load/unload), `LocalDriver` (PhysicalFileSystem + SubFileSystem), `AdminEndpoints` (`/api/admin/storage|driver|user/*`). 28/28 tests passing. |
| 2026-06-23 | Phase 4 | Filesystem: `FileSystemService` (list/get/mkdir/rename/move/copy/remove/put/dirs/search/archive/link via Zio MountFileSystem), `FsEndpoints` (`/api/fs/*`), `ClaimsPrincipal.ToUser()` extension. 50/50 tests passing. |
| 2026-06-23 | Phase 7 | Admin: `SettingService` (CRUD + public filter + archive exts), `MetaService` (path metadata CRUD), `MessageService` (admin↔user messaging), `ScanService` (background directory scan with progress). Extended `AdminEndpoints` (user/set_password, setting, meta, message, scan), `MeEndpoints` (message unread/list/read), `FsEndpoints` (/scan). Added `Message` model + DbSet. 76/76 tests passing. |
| 2026-06-23 | Phase 10-12 | S3 Gateway (`S3Endpoints` — list buckets, get/put/head/delete objects, JWT passthrough auth), WebDAV Server (`WebDavEndpoints` — OPTIONS/PROPFIND/PROPPATCH/GET/HEAD/PUT/MKCOL/DELETE/MOVE/COPY/LOCK/UNLOCK, Basic auth via `UserService`), FTP hosted service (`FtpHostedService` + `OpenListFtpMembershipProvider` + `ZioUnixFileSystem` bridge via FubarDev.FtpServer), SFTP stub (`SftpHostedService` — SSH.NET has no server). Fixed `FileSystemService.PutStreamAsync` sync IO → async. `TestWebAppFactory` integration test infra with per-factory temp SQLite DB + Local storage mount. 116/116 tests passing. |
| 2026-06-24 | Phase 6 | Tasks & Offline Download: `TaskService` (in-memory task store with progress tracking, cancel/retry/delete/clear), `TaskEndpoints` (`/api/task/undone|done|info|cancel|delete|clear_done|retry`), `FsEndpoints` additions (`/api/fs/add_offline_download` + 4 `/api/fs/torrent/*` 501 stubs). 128/128 tests passing (12 new `TaskServiceTests`). |
