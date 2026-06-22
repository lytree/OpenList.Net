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

- `/api/fs/torrent/*` — 4 endpoints return 501
- `/api/auth/sso/*` — SSO login
- `/api/authn/*` — WebAuthn
- `/ap/*path`, `/ae/*path` — signed archive proxy/extract (return 501)
- `/sad/*` — sharing archive download (returns 404)
- `IFileSystemArchiveDecompress` — interface defined, no implementation
- `IFileSystemDirectUploader` — interface defined, stubbed
