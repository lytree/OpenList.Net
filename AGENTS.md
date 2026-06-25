# OpenList.Net — Agent Instructions

## Build & Test

- **Build**: `dotnet build`
- **Run**: `dotnet run --project src/OpenList.Server` (listens `http://0.0.0.0:5244` by default)
- **Test**: `dotnet test` — uses **TUnit** (not NUnit), async convention: `await Assert.That(actual).Is(expected)`
- **Lint**: `dotnet format`
- **Dev server** (with frontend proxy): `dotnet run` in server + `pnpm dev` in `frontend/`

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
| `AuthEndpoints.cs` (AuthnEndpoints) | `/api/authn` | Mixed (WebAuthn stubs) |
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

### Stubbed (route exists, returns 501 "not implemented")

- `/api/auth/sso/*` — 3 SSO stubs (`/sso/providers`, `/sso/{provider}`, `/sso/{provider}/callback`). Go has 4 different routes (`/auth/sso`, `/auth/sso_callback`, `/auth/get_sso_id`, `/auth/sso_get_token`)
- `/api/authn/*` — 4 WebAuthn stubs (`/begin/register`, `/finish/register`, `/begin/login`, `/finish/login`). Go also has `/delete_authn` and `/getcredentials` (missing)
- `/api/fs/torrent/*` — 4 stubs (`/list`, `/info`, `/add`, `/cancel`), **route names do NOT match Go**. Go routes: `/torrent/parse`, `/torrent/upload_parse`, `/torrent/rapid_upload`, `/torrent/generate` (POST)

### Completely missing (no route registered)

**Sharing system** (Program.cs registers `MapShareApi` + `MapSharingDownloadApi`):
- ~~`/api/share/*` — 7 sharing-management routes~~ **Implemented** (`list`, `get`, `create`, `update`, `delete`, `enable`, `disable`). Lookups use the user-visible share **code** (frontend `?id=`), not the db primary key. Service: `SharingService` / `ISharingService`.
- ~~`/sd/:sid[/*path]` — sharing signed download (GET + HEAD)~~ **Implemented** (`ShareDownloadEndpoints.MapSharingDownloadApi`). Public; verifies share code + optional `?pwd=` + expiration/disable/max-accessed; bumps `Accessed` counter; streams file via `FileDownloadHelper` (Range-supported). Path mapping: `share.Path` + URL relative path (single-file/dir shares; multi-file matching is P2).
- `/sad/:sid[/*path]` — sharing signed archive extract (GET + HEAD) — *not yet implemented*

**Signed archive routes** (Go `/ad/`, `/ap/`, `/ae/`):
- `/ad/*path` — archive signed download (GET + HEAD)
- `/ap/*path` — archive signed proxy (GET + HEAD)
- `/ae/*path` — archive signed internal extract (GET + HEAD)

~~**Sign endpoint HEAD methods**: Go registers `HEAD /d/*path` and `HEAD /p/*path`; .NET only has `GET`.~~ **Implemented** — `SignEndpoints` now registers `HEAD /d/{**path}` and `HEAD /p/{**path}` via `MapMethods`, used by media players/download managers to probe size/Range support.

**Auth/Me routes**:
- `/api/auth/login/ldap` — LDAP login
- ~~`/api/me/sshkey/{list,add,delete}` — SSH public-key management~~ **Implemented** (`MeEndpoints` + `SshKeyService`): `GET /me/sshkey/list` returns paged `{content, total}`; `POST /me/sshkey/add` body `{title, key}` computes `SHA256:base64` fingerprint; `POST /me/sshkey/delete?id=` returns remaining keys. `SshKey` model gained `LastUsedTime` (migration via `ALTER TABLE x_sshkeys ADD COLUMN`).
- `/api/authn/{delete_authn,getcredentials}` — WebAuthn credential delete/list

**Fs routes**:
- `/api/fs/regex_rename` — regex batch rename (Go endpoint; frontend uses `/fs/batch_rename` instead — see below)
- ~~`/api/fs/recursive_move` — recursive move~~ **Implemented** (`FsEndpoints.RecursiveMoveAsync`; Go alias for single-src move)
- ~~`/api/fs/form` — form upload~~ **Implemented** (`PUT /api/fs/form`, multipart; path from `File-Path` header. `/put` still handles stream uploads)
- ~~`/api/fs/get_direct_upload_info` — direct (client-side) upload info~~ **Implemented** (`POST /api/fs/get_direct_upload_info`)
- ~~`/api/fs/batch_rename`~~ **Implemented** (`POST /fs/batch_rename` body `{src_dir, rename_objects:[{src_name,new_name}]}`; prepends `src_dir` to form full paths, then calls `BatchRenameAsync`. The older `/rename_batch` endpoint with `{items:[{src_name,new_name}]}` full paths is retained for API compat.)
- `/api/fs/other` — misc fs operations

**Admin routes**:
- ~~`/api/admin/user/del_cache` — clear user cache~~ **Implemented** (`POST /admin/user/del_cache?id=`; `UserService.DelCacheAsync` is a no-op placeholder until a cache layer is added).
- ~~`/api/admin/user/sshkey/{list,delete}` — admin SSH-key management~~ **Implemented** (`AdminEndpoints`): `GET /admin/user/sshkey/list?uid=` and `POST /admin/user/sshkey/delete?uid=&id=`; reuses `SshKeyService` with `isAdmin: true` so admins can manage any user's keys.
- ~~`/api/admin/setting/delete`, `/setting/default`, `/setting/reset_token`~~ **Implemented** (`AdminEndpoints`): `delete` removes a key; `default?group=` restores `DbInitializer.DefaultSettings` for the group; `reset_token` rotates the JWT secret (updates singleton `AppConfiguration` + persists to `jwt_secret` file + updates bearer `TokenValidationParameters` via `IOptionsMonitor` so existing tokens are invalidated immediately).
- `/api/admin/setting/set_{aria2,qbit,transmission,115,115_open,123_pan,123_open,pikpak,thunder,thunderx,thunder_browser}` — 11 offline-download-tool config routes
- ~~`/api/admin/index/{build,update,stop,clear,progress}` — search-index management (5 routes)~~ **Implemented** — `SearchService` (singleton) walks the filesystem via `IFileSystemService.ListAsync` in a fire-and-forget `Task.Run`, tracking progress in-memory. Routes: `POST /admin/index/{build,update,stop,clear}` (body `{paths, max_depth}` for build/update), `GET /admin/index/progress` returns `{obj_count, is_done, last_done_time, error}` matching the frontend `Progress` type. `ISearchService`/`IndexProgress` updated: `BuildIndexAsync`/`UpdateIndexAsync` now take `(IReadOnlyList<string> paths, int maxDepth)`; `IndexProgress` gained `LastDoneTime` + `Error`. Indexed entries are not yet persisted (progress/control surface only); `SearchAsync` delegates to `IFileSystemService.SearchAsync`.
- ~~`/api/admin/scan/{stop,progress}` — only `/scan/{path}` (start) exists~~ **Implemented** — `POST /admin/scan/start` (body `{path, limit}`), `POST /admin/scan/stop`, `GET /admin/scan/progress` (returns `{obj_count, is_done}` matching frontend).

**Task routes**:
- ~~`/api/task/cancel_some`, `/delete_some`, `/retry_some` — batch operations~~ **Implemented** — TaskEndpoints refactored to frontend's `/api/task/{type}/...` path pattern. Batch ops take `string[]` body, return `{data: {tid: errorMsg}}` (empty = all succeeded). Added `ClearSucceededAsync`, `RetryFailedAsync`, `CancelSomeAsync`, `DeleteSomeAsync`, `RetrySomeAsync` to `ITaskService`/`TaskService`.
- ~~`/api/task/clear_succeeded`, `/retry_failed`~~ **Implemented** (see above).

**Misc public routes**:
- ~~`/i/:link_name` — Plist (iOS video streaming)~~ **Implemented** (`MiscEndpoints.MapMiscApi`; decodes base64 `[url]/[name@identifier]`, emits Apple plist XML for IPA install)
- ~~`/favicon.ico`, `/robots.txt`, `/manifest.json`~~ **Implemented** (`MiscEndpoints`): `/manifest.json` returns PWA manifest using `site_title` from settings; `/robots.txt` returns `User-agent: *\nAllow: /`; `/favicon.ico` 302-redirects to the `logo` setting (404 if unset).

### Driver extension interfaces (defined in `IFileSystemDriverExtensions.cs`, no driver implements)

- `IFileSystemArchiveReader` — `ListArchiveEntriesAsync`; no implementation (archive list/meta endpoints rely on this)
- `IFileSystemArchiveDecompress` — `DecompressAsync`; no implementation
- `IFileSystemDirectUploader` — `GetDirectUploadInfoAsync`; no implementation
- `IFileSystemWithDetails` — no implementation

Implemented extensions: `IFileSystemLinkProvider` (WebDAV, S3), `IFileSystemUploader` (WebDAV, S3, FTP, SFTP).
