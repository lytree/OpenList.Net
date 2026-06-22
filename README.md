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

## Phase 2 — Auth & User Management

Core auth pipeline so the frontend can log in. No storage ops yet.

- [ ] `IUserService` impl + `UserService` (EF Core queries)
- [ ] `IAuthService` impl + `JwtService` (token issue/verify, 2FA TOTP via Otp.NET)
- [ ] JWT middleware: `OnMessageReceived` strips no-prefix token, validates, sets `HttpContext.User`
- [ ] `AuthEndpoints.cs` — `/api/auth/*`:
  - [ ] `POST /api/auth/login` — username/password → token (+ 2FA flow)
  - [ ] `POST /api/auth/login/2fa` — TOTP verification
  - [ ] `POST /api/auth/logout`
  - [ ] `GET  /api/auth/me` — current user
  - [ ] `POST /api/auth/2fa/generate` — generate TOTP secret + QR (QRCoder)
  - [ ] `POST /api/auth/2fa/verify`
  - [ ] `POST /api/auth/hash_password` — admin only
- [ ] `MeEndpoints.cs` — `/api/me/*`:
  - [ ] `POST /api/me/update` — update profile (username, password)
  - [ ] `POST /api/me/2fa`
  - [ ] `GET  /api/me/sessions`
  - [ ] `DELETE /api/me/sessions/:id`
- [ ] Admin user auto-seed on first run (`AdminUsername`/`AdminPassword` config)
- [ ] Tests: `UserServiceTests`, `JwtServiceTests`

## Phase 3 — Storage Management & Driver Loading

Admin CRUD for storages + driver instantiation.

- [ ] `IStorageService` impl + `StorageService`
- [ ] `ZioMountService.LoadAllAsync` — instantiate drivers, call `InitAsync`, register in `MountFileSystem`
- [ ] `AdminEndpoints.cs` — `/api/admin/*` (admin role `2`):
  - [ ] `GET  /api/admin/storage/list`
  - [ ] `GET  /api/admin/storage/get?id=`
  - [ ] `POST /api/admin/storage/create`
  - [ ] `POST /api/admin/storage/update`
  - [ ] `POST /api/admin/storage/delete`
  - [ ] `POST /api/admin/storage/disable` / `enable`
  - [ ] `POST /api/admin/storage/sort`
  - [ ] `GET  /api/admin/storage/driver/list` — available drivers + config schema
  - [ ] `GET  /api/admin/storage/driver/info?driver=`
  - [ ] `POST /api/admin/storage/load_all` — reload all storages
  - [ ] `POST /api/admin/storage/drop_index`
- [ ] `LocalDriver` full impl (mount directory, read/write, links)
- [ ] Tests: `StorageServiceTests`, `LocalDriverTests`

## Phase 4 — Filesystem Operations

The core `/api/fs/*` surface — list, mkdir, rename, copy, move, delete, upload, download.

- [ ] `IFileSystemService` impl + `FileSystemService` (routes to mounted `ZioDriverBase`)
- [ ] `FsEndpoints.cs` — `/api/fs/*`:
  - [ ] `POST /api/fs/list` — `{ path, password?, page?, per_page?, refresh? }`
  - [ ] `POST /api/fs/get` — `{ path, password? }` (file/folder detail)
- [ ] `POST /api/fs/mkdir`
  - [ ] `POST /api/fs/rename`
  - [ ] `POST /api/fs/copy`
  - [ ] `POST /api/fs/move`
  - [ ] `POST /api/fs/remove`
  - [ ] `PUT  /api/fs/put` — upload (multipart, `IFileSystemUploader`)
  - [ ] `GET  /api/fs/get?id=&name=` — direct download stream
  - [ ] `POST /api/fs/link` — generate signed link (`/d/`)
  - [ ] `POST /api/fs/dirs` — recursive directory list (for move/copy dialog)
  - [ ] `POST /api/fs/rename_batch`
  - [ ] `POST /api/fs/move_batch`
  - [ ] `POST /api/fs/copy_batch`
  - [ ] `POST /api/fs/remove_batch`
  - [ ] `POST /api/fs/recyclebin/clear`
  - [ ] `POST /api/fs/recyclebin/list`
  - [ ] `POST /api/fs/recyclebin/restore`
- [ ] `SignEndpoints.cs` — signed URL handlers:
  - [ ] `GET /d/*path` — signed download
  - [ ] `GET /p/*path` — signed preview/proxy
  - [ ] `GET /ad/*path` — signed archive download (501 for now)
  - [ ] `GET /ap/*path` — signed archive proxy (501 for now)
  - [ ] `GET /ae/*path` — signed archive extract (501 for now)
- [ ] Tests: `FileSystemServiceTests` (against in-memory `MemoryFileSystem`)

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

- [ ] `ITaskService` impl + `TaskService` (in-memory + persistent task store)
- [ ] `TaskEndpoints.cs` — `/api/task/*`:
  - [ ] `GET  /api/task/undone`
  - [ ] `GET  /api/task/done`
  - [ ] `POST /api/task/cancel?tid=`
  - [ ] `POST /api/task/delete?tid=`
  - [ ] `POST /api/task/clear_done`
  - [ ] `POST /api/task/retry?tid=`
- [ ] Offline download tools (`IOfflineDownloadTool`):
  - [ ] `POST /api/fs/add_offline_download` — aria2/qBittorrent/115/pikpak/thunder
  - [ ] `GET  /api/fs/torrent/*` — 4 endpoints (501 for now)
- [ ] Tests: `TaskServiceTests`

## Phase 7 — Admin: Users, Settings, Messages, Meta

- [ ] `AdminEndpoints.cs` (continued):
  - [ ] `GET  /api/admin/user/list`
  - [ ] `GET  /api/admin/user/get?id=`
  - [ ] `POST /api/admin/user/create`
  - [ ] `POST /api/admin/user/update`
  - [ ] `POST /api/admin/user/delete`
  - [ ] `POST /api/admin/user/cancel_2fa`
  - [ ] `POST /api/admin/user/set_password`
  - [ ] `GET  /api/admin/setting/list`
  - [ ] `POST /api/admin/setting/save`
  - [ ] `GET  /api/admin/setting/get?keys=`
  - [ ] `POST /api/admin/setting/set`
  - [ ] `POST /api/admin/meta/list`
  - [ ] `POST /api/admin/meta/get`
  - [ ] `POST /api/admin/meta/save`
  - [ ] `POST /api/admin/meta/delete`
- [ ] `IMessageService` impl + `MessageService`:
  - [ ] `GET  /api/admin/message/list`
  - [ ] `GET  /api/admin/message/get?id=`
  - [ ] `POST /api/admin/message/create`
  - [ ] `POST /api/admin/message/update`
  - [ ] `POST /api/admin/message/delete`
  - [ ] `GET  /api/me/message/unread`
  - [ ] `GET  /api/me/message/list`
  - [ ] `POST /api/me/message/read`
- [ ] `IScanService` impl + `ScanService`:
  - [ ] `POST /api/admin/scan/:path`
  - [ ] `POST /api/fs/scan`
- [ ] Tests: `UserServiceTests`, `SettingServiceTests`, `MessageServiceTests`

## Phase 8 — Drivers: S3, WebDAV, FTP/SFTP/SMB

- [ ] `S3Driver` (Minio client) — read/write/list/upload
- [ ] `WebDAVDriver` (WebDav.Client) — read/write/list/upload
- [ ] `FtpDriver` (FluentFTP)
- [ ] `SftpDriver` (SSH.NET)
- [ ] `SmbDriver` (SMBLibrary)
- [ ] Driver extension interfaces: `IFileSystemUploader`, `IFileSystemLinkProvider`, `IFileSystemWithDetails`
- [ ] Tests: each driver against testcontainers / mock servers

## Phase 9 — Cloud Drivers

- [ ] `AliyundriveDriver`, `BaiduNetdiskDriver`, `115Driver`, `QuarkDriver`, `PikpakDriver`, `ThunderDriver`, `OneDriveDriver`, `GoogleDriveDriver`, `DropboxDriver`, `TeraboxDriver`, `WeiyunDriver`, `MegaDriver`, `LanzouDriver`
- [ ] Token refresh plumbing (per-driver `TokenManager`)
- [ ] Tests: mock HTTP responses per driver

## Phase 10 — S3 Gateway

- [ ] `S3Endpoints.cs` — `/s3` — S3-compatible API (list buckets, get/put/delete objects)
- [ ] Auth: AWS SigV4 or passthrough to OpenList JWT
- [ ] Tests: `S3EndpointsTests` with `Amazon.S3.TestUtility`

## Phase 11 — WebDAV Server

- [ ] `WebDavEndpoints.cs` — `/dav` — full WebDAV (PROPFIND, GET, PUT, MKCOL, DELETE, MOVE, COPY, LOCK/UNLOCK)
- [ ] Basic auth → OpenList user
- [ ] Tests: `WebDavEndpointsTests`

## Phase 12 — FTP/SFTP Hosted Servers

- [ ] FTP hosted service (FubarDev.FtpServer) — disabled by default
- [ ] SFTP hosted service (SSH.NET) — disabled by default
- [ ] Auth bridge to OpenList users
- [ ] Virtual filesystem via Zio

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
