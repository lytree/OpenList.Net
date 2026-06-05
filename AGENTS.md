# OpenList.Net — Agent Instructions

## API Style

**All APIs are Minimal APIs** (no controllers). The `Controllers/` directory exists but is empty — all endpoints live in `src/OpenList.Server/Endpoints/` as static extension methods (`MapAuthApi`, `MapFsApi`, etc.) called from `Program.cs`.

JSON uses **PascalCase** (`PropertyNamingPolicy = null`) — non-standard for ASP.NET Core. New endpoints must also use PascalCase.

All responses are wrapped in `Resp<T>` (see `src/OpenList.Server/Models/Resp.cs:3`):
```
{ "Code": 200, "Message": "success", "Data": ... }
```

## Reference Implementation

C# port of [OpenList](https://github.com/OpenListTeam/OpenList) (Go/Gin, v4). The Go source is authoritative for API behavior, route structure, and driver semantics.

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
| `src/OpenList.Core` | Models (`IObj`, `User`, `Storage`), driver interfaces (`IDriver`, role-based sub-interfaces), service interfaces (`IService`) |
| `src/OpenList.Drivers` | Storage driver implementations — `Local/`, `S3/`, `WebDAV/`, `FtpSftpSmb/`, `Cloud/`, `Stubs/` |
| `src/OpenList.Infrastructure` | EF Core `DbContext`, service implementations, JWT auth, `DriverFactory`, SQLite/MySQL/PostgreSQL/MSSQL support |
| `src/OpenList.Server` | ASP.NET Core entrypoint (`Program.cs`), `Endpoints/` (Minimal APIs), `Middleware/`, `Models/` (DTOs + Resp<T>) |
| `tests/OpenList.Tests` | xUnit test project (placeholder — only UnitTest1.cs stub) |

## Driver System

**Auto-discovery**: `DriverFactory` (`src/OpenList.Infrastructure/Services/DriverFactory.cs:29`) scans assemblies starting with `"OpenList"` for `IDriver` implementations. Adding a driver only requires creating a class implementing `IDriver` in `src/OpenList.Drivers`.

**Role-based interfaces**: Drivers declare capabilities by implementing optional sub-interfaces:
- `IDriverGetter`, `IDriverMkdir`/`IDriverMkdirResult`, `IDriverMove`/`IDriverMoveResult`, `IDriverRename`/`IDriverRenameResult`, `IDriverCopy`/`IDriverCopyResult`, `IDriverRemove`, `IDriverPut`/`IDriverPutResult`, `IDriverPutURL`, `IDriverOther`, `IDriverArchiveReader`, `IDriverArchiveDecompress`, `IDriverWithDetails`, `IDriverDirectUploader`

Base classes at `src/OpenList.Core/Drivers/Base/DriverBase.cs:9`: `DriverBase` (abstract), `RootPathDriver`, `RootIdDriver`.

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
- **Database**: SQLite by default (`data/data.db`); supports MySQL, PostgreSQL, MSSQL via config. DB init uses `EnsureCreatedAsync()` — no migrations, schema dropped + recreated on schema changes. Default table prefix: `x_`
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
- `IDriverArchiveDecompress` — interface defined but no implementation
- `IDriverDirectUploader` — interface defined but stubbed
