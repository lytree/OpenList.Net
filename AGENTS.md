# OpenList.Net — Agent Instructions

## API Style

**All new APIs must use Minimal APIs** (not controllers). The existing `Controllers/` directory contains legacy code — do not add new controllers. Register endpoints under `/api` via `MapGet`/`MapPost`/etc. in `Program.cs` or extension methods.

## Reference Implementation

This is a C# port of [OpenList](https://github.com/OpenListTeam/OpenList) (Go/Gin, v4). The Go source is authoritative for API behavior, route structure, and driver semantics.

## Target Framework

`net10.0` set centrally via `Directory.Build.props`. All projects inherit it — no per-csproj `TargetFramework` needed.

## Central Package Management

`Directory.Packages.props` defines all NuGet versions. `ManagePackageVersionsCentrally=true` is in `Directory.Build.props`. Reference packages without a version attribute in `.csproj` files.

## Project Structure

| Project | Purpose |
|---|---|
| `src/OpenList.Core` | Models, driver interfaces (`IDriver`), service interfaces (`IService`) |
| `src/OpenList.Drivers` | Storage driver implementations (Local, S3, WebDAV, FTP, SFTP, SMB, cloud stubs) |
| `src/OpenList.Infrastructure` | EF Core DbContext, service implementations, auth, driver factory |
| `src/OpenList.Server` | ASP.NET Core Web API entrypoint (`Program.cs`), controllers (legacy), middleware |
| `tests/OpenList.Tests` | xUnit test project (placeholder) |

## Driver Auto-Discovery

`DriverFactory` (`src/OpenList.Infrastructure/Services/DriverFactory.cs:31`) scans assemblies whose name starts with `"OpenList"` for `IDriver` implementations. Adding a driver requires only creating a class implementing `IDriver` in `src/OpenList.Drivers` — no registration needed.

## API Controllers (Legacy — Do Not Add New)

The following controllers exist in `src/OpenList.Server/Controllers/`. New endpoints must be Minimal APIs:

- `AuthController` — login, 2FA
- `FsController` — list, get, mkdir, rename, move, copy, remove, put
- `AdminController` — user, storage, meta, setting
- `ShareController` — CRUD
- `TaskController` — list, info, cancel, retry, delete
- `MeController` — user profile, SSH keys
- `PublicController` — public settings
- `SignController` — sign-based download/proxy
- `SharingDownloadController` — sharing downloads
- `S3Controller` — S3-compatible API (if enabled)
- `WebDavController` — WebDAV (if enabled)

## Defaults & Quirks

- **JSON**: PascalCase (`PropertyNamingPolicy = null`) — non-standard for ASP.NET Core; new Minimal APIs must also use PascalCase
- **Auth**: JWT Bearer with configurable secret (auto-generated `Guid` if empty in config)
- **CORS**: Wide open (`AllowAnyOrigin/Method/Header`)
- **Rate limit**: 100 req/min per IP (`RateLimitMiddleware`)
- **Database**: SQLite by default (`data/data.db`); supports MySQL, PostgreSQL, MSSQL via config
- **DB init**: `EnsureCreatedAsync()` — no migrations, schema dropped + recreated on schema changes
- **Swagger**: Available at `/swagger` in Development only
- **SPA fallback**: Serves `index.html` from `DistDir` if configured
- **Server listens on**: `http://localhost:5211` (dev) / `http://0.0.0.0:5244` (default config)

## Key Commands

```powershell
dotnet build
dotnet run --project src/OpenList.Server
dotnet test
```

## Not Yet Implemented (from Go reference)

- `/api/fs/search`, `/api/fs/dirs`, `/api/fs/link`, `/api/fs/batch_rename`, `/api/fs/regex_rename`, `/api/fs/recursive_move`, `/api/fs/remove_empty_directory`, `/api/fs/add_offline_download`, `/api/fs/archive/decompress`, `/api/fs/torrent/*`, `/api/fs/get_direct_upload_info`
- `/api/auth/sso/*`, `/api/authn/*` — SSO, WebAuthn
- `/api/admin/message/*`, `/api/admin/index/*`, `/api/admin/scan/*`
- `/d/*path`, `/p/*path` — sign-based download/proxy routes
- `/ad/*path`, `/ap/*path`, `/ae/*path` — archive download/proxy/extract
