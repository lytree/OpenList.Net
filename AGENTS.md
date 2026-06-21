# OpenList.Net — Agent Instructions

## API Style

**All APIs are Minimal APIs** (no controllers). The `Controllers/` directory exists but is empty — all endpoints live in `src/OpenList.Server/Endpoints/` as static extension methods (`MapAuthApi`, `MapFsApi`, etc.) called from `Program.cs`.

JSON uses **camelCase** (`PropertyNamingPolicy = JsonNamingPolicy.CamelCase`) — standard ASP.NET Core default. New endpoints must also use camelCase.

All responses are wrapped in `Resp<T>` (see `src/OpenList.Server/Models/Resp.cs:3`):
```
{ "code": 200, "message": "success", "data": ... }
```

## Reference Implementation

C# port of [OpenList](https://github.com/OpenListTeam/OpenList)(local: F:\Code\Dotnet\OpenList) (Go/Gin, v4). The Go source is authoritative for API behavior, route structure, and driver semantics.

## Build System

- **.slnx** format (OpenList.slnx) — no legacy `.sln`
- `net10.0` set centrally in `Directory.Build.props` — no per-csproj `TargetFramework`
- **Central Package Management**: `Directory.Packages.props` defines all versions; reference packages without version in `.csproj`

```powershell
dotnet build
dotnet run --project src/OpenList.Server
dotnet test
```

## Project Structure

| Project | Purpose |
|---|---|
| `src/OpenList.Core` | Models (`IObj`, `User`, `Storage`), `ZioDriverBase` and extension interfaces, service interfaces (`IService`) |
| `src/OpenList.Drivers` | Storage driver implementations — `Local/`, `S3/`, `WebDAV/`, `FtpSftpSmb/`, `Cloud/`, `Stubs/` |
| `src/OpenList.Infrastructure` | EF Core `DbContext`, service implementations, JWT auth, `ZioMountService`, PostgreSQL support |
| `src/OpenList.Server` | ASP.NET Core entrypoint (`Program.cs`), `Endpoints/` (Minimal APIs), `Middleware/`, `Models/` (DTOs + Resp<T>) |
| `tests/OpenList.Tests` | NUnit test project (placeholder — only UnitTest1.cs stub) |

## Driver System

**Auto-discovery**: `ZioMountService` (`src/OpenList.Infrastructure/Services/ZioMountService.cs`) scans assemblies starting with `"OpenList"` for `ZioDriverBase` implementations. Adding a driver only requires creating a class inheriting from `ZioDriverBase` in `src/OpenList.Drivers`.

**Zio-based architecture**: All drivers inherit from `ZioDriverBase` (`src/OpenList.Core/Drivers/Base/ZioDriverBase.cs`), which extends Zio's `FileSystem` base class, providing a unified `IFileSystem` abstraction. Drivers are mounted via `MountFileSystem`.

**Extension interfaces**: Drivers declare capabilities by implementing optional extension interfaces:
- `IFileSystemLinkProvider`, `IFileSystemUploader`, `IFileSystemWithDetails`, `IFileSystemArchiveReader`, `IFileSystemArchiveDecompress`, `IFileSystemDirectUploader`

## Endpoint Organization

Each API group maps to a file in `src/OpenList.Server/Endpoints/`:

| File | Route Group | Auth |
|---|---|---|
| `AuthEndpoints.cs` | `/api/auth` | Mixed (login public, rest auth'd) |
| `FsEndpoints.cs` | `/api/fs` | Mixed |
| `AdminEndpoints.cs` | `/api/admin` | Admin role (`RequireRole("2")`) |
| `ShareEndpoints.cs` | `/api/share` | Mixed |
| `TaskEndpoints.cs` | `/api/task` | Auth'd |
| `MeEndpoints.cs` | `/api/me` | Auth'd |
| `PublicEndpoints.cs` | `/api/public` | Public |
| `SignEndpoints.cs` | `/d/`, `/p/`, `/ad/`, `/ap/`, `/ae/` | Public (sign-verified) |
| `SharingDownloadEndpoints.cs` | `/sd/`, `/sad/` | Public (password-protected) |
| `S3Endpoints.cs` | `/s3` | Mixed |
| `WebDavEndpoints.cs` | `/dav` | Mixed |

## Defaults & Quirks

- **Auth**: JWT Bearer with configurable secret (auto-generated 16-char `Guid` if empty in config)
- **CORS**: `AllowAnyOrigin/Method/Header` (also a custom `CorsMiddleware` handles OPTIONS preflight)
- **Rate limit**: 100 req/min per IP (`RateLimitMiddleware` at `src/OpenList.Server/Middleware/Middleware.cs:105`)
- **Database**: PostgreSQL only (via Npgsql). DB init uses `MigrateAsync()` with EF Core migrations, falling back to `EnsureCreatedAsync()` for databases without migrations history. Default table prefix: `x_`
- **Swagger**: at `/swagger` in Development only
- **SPA fallback**: serves `index.html` from `DistDir` if configured
- **Server listens on**: `http://localhost:5211` (dev) / `http://0.0.0.0:5244` (default config)
- **Kestrel body limit**: 10GB (for WebDAV uploads)

## Not Yet Implemented (from Go reference)

- `/api/fs/torrent/*` — all 4 endpoints return 501
- `/api/auth/sso/*` — SSO login
- `/api/authn/*` — WebAuthn
- `/ap/*path`, `/ae/*path` — signed archive proxy/extract (return 501)
- `/sad/*` — sharing archive download (returns 404)
- `IFileSystemArchiveDecompress` — interface defined but no implementation
- `IFileSystemDirectUploader` — interface defined but stubbed
