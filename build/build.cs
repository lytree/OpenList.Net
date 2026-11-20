#!/usr/bin/env dotnet
#:sdk Cake.Sdk@6.2.0
// Cake build script for OpenList.Net (project-based Cake.Sdk).
//
// Run a task:
//   dotnet run --project build -- --target=Test
//   dotnet run --project build -- --target=Format
//   dotnet run --project build -- --target=Version                            # compute & print GitVersion
//   dotnet run --project build -- --target=Package --rid=linux-x64 --archive=tar.gz
//   dotnet run --project build -- --target=CI                                 # framework-dependent bundle
//   dotnet run --project build -- --target=Bundle --frontend-dist=frontend    # use committed frontend, skip fetch
//   dotnet run --project build -- --target=Publish --semver=1.2.3             # publish with injected semver
//   dotnet run --project build -- --target=Package --app=desktop --rid=win-x64  # Desktop GUI + server bundle
//
// Arguments:
//   --configuration=Release    MSBuild configuration
//   --output=artifacts         Artifact root
//   --app=server|desktop       What to publish (default: server). desktop = three exes in the
//                              bundle root + frontend/dist; requires --rid
//   --rid=<rid>                Target runtime; empty = framework-dependent (server only)
//   --dest=<path>              Only for --target=Runtime: where to create the dotnet/ root
//   --frontend-dist=<path>     Frontend dist source (default: frontend/dist, built from this repo)
//   --frontend-version=<tag>   Legacy escape hatch: download an upstream OpenList-Frontend release
//   --archive=zip|tar.gz       Package format (Package target)
//   --semver=<x.y.z>           Pre-computed semver (skips local GitVersion; CI uses this)
//
// Runtime layout (RID builds only):
//   Every app in a bundle is published **framework-dependent, RID-specific, single-file** — the
//   managed assemblies and native libraries are inside the exe, the runtime is not — and the
//   host is taught where to look with AppHostDotNetSearch=AppRelative + AppHostRelativeDotNet.
//   One .NET install layout (host/fxr/<ver>/ + shared/Microsoft.NETCore.App|Microsoft.
//   AspNetCore.App/<ver>) is staged once per bundle as `dotnet/` and shared by every
//   executable in it:
//
//     desktop bundle (--app=desktop)      headless bundle (--app=server)
//       OpenList.Desktop[.exe]  ─┐          app/OpenList.Server[.exe] ─┐
//       OpenList.Server[.exe]   ─┼─→ dotnet/  app/appsettings.json     ─┼─→ ../dotnet
//       openlist[.exe]          ─┘          cli/openlist[.exe]         ┘
//       config/appsettings.json             dotnet/
//       dotnet/                             frontend/dist/
//       frontend/dist/
//
//   单文件是"根目录干净"的前提：三个发布输出合并到同一目录原本会留下 ~150 个散落的
//   .dll/.pdb，现在每个 app 只剩一个 exe（程序集已在里面），根目录只有三个可执行文件。
//   运行时仍然只有一份 —— 每个 exe 按自己相对位置解析 `dotnet`（桌面端三个都在根）。
//   Before the shared root existed, every folder carried its own ~106 MB runtime
//   (desktop bundle: three copies / 494 MB). `--target=Runtime` builds just the `dotnet/`
//   root, which is what CI calls per matrix leg.

using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

var target        = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var output        = Argument("output", "artifacts");
var app           = Argument("app", "server");
var rid           = Argument("rid", "");
var dest          = Argument("dest", "");
var frontendDist  = Argument("frontend-dist", "");
var frontendVer   = Argument("frontend-version", "");
var archive       = Argument("archive", "zip");
// Allow callers to inject a pre-computed semver (e.g. CI workflow that runs GitVersion itself).
// When empty, the Version task computes it locally via `dotnet gitversion`.
var semverArg     = Argument("semver", "");

const string SolutionFile    = "OpenList.slnx";
const string ServerProject   = "src/OpenList.Server/OpenList.Server.csproj";
const string CliProject      = "src/OpenList.Cli/OpenList.Cli.csproj";
const string DesktopProject  = "src/OpenList.Desktop/OpenList.Desktop.csproj";
const string UpstreamRepo    = "OpenListTeam/OpenList-Frontend";
// The SPA is ours since M5: it is built from `frontend/` (Vue 3) and lands in `frontend/dist`.
const string RepoFrontendDist = "frontend/dist";
// Bundle-relative folder holding the single shared .NET install layout (see the header).
const string RuntimeRootName = "dotnet";

// Desktop bundles ship a GUI whose only runtime is the shared `dotnet/` root — there is no
// meaningful "GUI without a runtime" bundle, so --rid stays mandatory.
bool IsDesktopApp() => app.Equals("desktop", StringComparison.OrdinalIgnoreCase);

// Where the SPA dist comes from. Default is the in-repo build output (`frontend/dist`), because
// since M5 the frontend ships from this repository. `--frontend-dist` points elsewhere;
// `--frontend-version` opts into the legacy behaviour of downloading an upstream release tarball.
string FeDistDir() => string.IsNullOrEmpty(frontendDist) ? RepoFrontendDist : frontendDist;

// Computed by the Version task. Empty until that task runs; consumers (Build/Publish/Bundle/Package)
// treat empty as "no versioning" so local dev builds don't require GitVersion to be installed.
string _semver = semverArg;
string _assemblyVer = "";
string _fileVer = "";
string _infoVer = "";
string _fullSemVer = "";
string _sha = "";
string _commitDate = "";

// Bundle name carries the semver when available (e.g. openlist-net-1.2.3-linux-x64).
// Falls back to the non-versioned name when semver is not yet known (e.g. inside Setup).
// Bundle name carries the semver when available (e.g. openlist-net-1.2.3-linux-x64).
// Falls back to the non-versioned name when semver is not yet known (e.g. inside Setup).
string BundleName()
{
    if (IsDesktopApp())
        return string.IsNullOrEmpty(rid)
            ? "openlist-net-desktop" // unversioned placeholder; Bundle/Package guard on missing --rid
            : $"openlist-net-desktop-{rid}";
    return string.IsNullOrEmpty(rid)
        ? "openlist-net-framework-dependent"
        : $"openlist-net-{rid}";
}

string BundleNameVersioned()
{
    var baseName = BundleName();
    return string.IsNullOrEmpty(_semver) ? baseName : $"{baseName}-{_semver}";
}

//////////////////////////////////////////////////////////////////////
// SETUP
//////////////////////////////////////////////////////////////////////
Setup(_ =>
{
    Information($"Target={target} Configuration={configuration} Output={output} App={app}");
    Information(string.IsNullOrEmpty(rid)
        ? "Rid=<framework-dependent> (no runtime shipped, uses the host's .NET)"
        : $"Rid={rid} (apps framework-dependent + shared {RuntimeRootName}/ runtime root)");
    if (!string.IsNullOrEmpty(semverArg))
        Information($"Pre-injected semver={semverArg}");
});

//////////////////////////////////////////////////////////////////////
// VERSION
//////////////////////////////////////////////////////////////////////

// Ensure the GitVersion .NET tool is installed locally (.config/dotnet-tools.json).
void EnsureGitVersionTool()
{
    var exit = StartProcess("dotnet", $"tool restore");
    if (exit != 0)
        throw new InvalidOperationException($"dotnet tool restore exited with {exit}");
}

// Run `dotnet gitversion` and parse the JSON output into the version variables.
// Respects GitVersion.yml at the repo root. Uses -output json for parseable output
// and -nocache to avoid stale caches in CI runners.
void ComputeGitVersion()
{
    EnsureGitVersionTool();

    // Use System.Diagnostics.Process directly so we can capture stdout/stderr reliably
    // (Cake's ProcessSettings output redirection quirks vary across versions).
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = "gitversion -output json -nocache",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = System.IO.Directory.GetCurrentDirectory(),
    };
    var stdout = new System.Text.StringBuilder();
    var stderr = new System.Text.StringBuilder();
    using var proc = new System.Diagnostics.Process { StartInfo = psi };
    proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
    proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
    proc.Start();
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    proc.WaitForExit();
    var exit = proc.ExitCode;

    if (exit != 0)
        throw new InvalidOperationException(
            $"dotnet gitversion exited with {exit}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

    using var doc = JsonDocument.Parse(stdout.ToString());
    var root = doc.RootElement;

    string Get(string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

    _semver      = Get("SemVer");
    _fullSemVer  = Get("FullSemVer");
    _assemblyVer = Get("AssemblySemVer");
    _fileVer     = Get("AssemblySemFileVer");
    _infoVer     = Get("InformationalVersion");
    _sha         = Get("Sha");
    _commitDate  = Get("CommitDate");

    if (string.IsNullOrEmpty(_assemblyVer)) _assemblyVer = $"{_semver.Split('+')[0]}.0";
    if (string.IsNullOrEmpty(_fileVer))     _fileVer     = _assemblyVer;
    if (string.IsNullOrEmpty(_infoVer))     _infoVer     = string.IsNullOrEmpty(_fullSemVer) ? _semver : _fullSemVer;

    Information($"GitVersion: SemVer={_semver} FullSemVer={_fullSemVer}");
    Information($"  AssemblyVer={_assemblyVer} FileVer={_fileVer}");
    Information($"  InformationalVer={_infoVer} Sha={_sha} CommitDate={_commitDate}");
}

// Apply version properties to an MSBuild settings object.
// These match the MSBuild properties GitVersion normally injects via its /updateassemblyinfo path.
DotNetMSBuildSettings WithVersionProps(DotNetMSBuildSettings settings)
{
    if (string.IsNullOrEmpty(_semver)) return settings;
    return settings
        .WithProperty("Version", _semver)
        .WithProperty("VersionPrefix", _semver)
        .WithProperty("AssemblyVersion", _assemblyVer)
        .WithProperty("FileVersion", _fileVer)
        .WithProperty("InformationalVersion", _infoVer)
        .WithProperty("SourceRevisionId", _sha);
}

// Write a VERSION file (used by the runtime to display build info) into the publish output.
void WriteVersionMetadata(string publishDir)
{
    if (string.IsNullOrEmpty(_semver)) return;
    var lines = new[]
    {
        $"semver={_semver}",
        $"fullSemVer={_fullSemVer}",
        $"assemblyVer={_assemblyVer}",
        $"fileVer={_fileVer}",
        $"informationalVer={_infoVer}",
        $"sha={_sha}",
        $"commitDate={_commitDate}",
    };
    System.IO.File.WriteAllLines(System.IO.Path.Combine(publishDir, "VERSION"), lines);
}

//////////////////////////////////////////////////////////////////////
// HELPERS
//////////////////////////////////////////////////////////////////////

// Copy a directory tree preserving relative structure.
// NOTE: fully-qualify System.IO.* — Cake exposes Directory()/File()/Path aliases that clash.
void CopyTree(string src, string dst)
{
    foreach (var f in System.IO.Directory.GetFiles(src, "*", System.IO.SearchOption.AllDirectories))
    {
        var rel = System.IO.Path.GetRelativePath(src, f);
        var target = System.IO.Path.Combine(dst, rel);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        System.IO.File.Copy(f, target, overwrite: true);
    }
}

//////////////////////////////////////////////////////////////////////
// SHARED RUNTIME ROOT (dotnet/)
//////////////////////////////////////////////////////////////////////

// 一个 bundle 里所有可执行文件（desktop / server / cli）共用同一份 .NET 安装布局：
//
//   dotnet/host/fxr/<coreVer>/hostfxr.<dll|so|dylib>
//   dotnet/shared/Microsoft.NETCore.App/<coreVer>/...
//   dotnet/shared/Microsoft.AspNetCore.App/<aspVer>/...
//
// 这正是 apphost 在 AppHostRelativeDotNet 指向的目录里要找的东西（先 hostfxr，再由它按
// shared/<framework>/<version> 解析框架），与 `dotnet-install` 装出来的目录同构。
// 注意它和自包含发布输出**不是**一个形状：SC 输出是扁平的（hostfxr.dll 就躺在 exe 旁边），
// 所以「把 SC 目录当成 AppRelative 目标」是不行的。
//
// 内容直接取自 NuGet 的 runtime pack：`runtimes/<rid>/native` + `runtimes/<rid>/lib/<tfm>`，
// 其中 native 里除 hostfxr 之外的 14 个文件 + lib 下 174 个托管程序集，与机器上
// `shared/Microsoft.NETCore.App/<ver>/` 的 188 个文件逐一对应（已核对 win-x64 10.0.14）。
// 版本不猜：先用一次自包含还原把 pack 拉进缓存（CI 干净机器上本来就没有），再从
// project.assets.json 的 downloadDependencies 读出 SDK 实际选定的版本号。
string NuGetPackagesRoot()
{
    var fromEnv = System.Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
    return System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        ".nuget", "packages");
}

// Read the runtime-pack versions the SDK resolved for this RID out of the restore output.
// `[10.0.14, 10.0.14]` → `10.0.14` (the SDK pins an exact range, so both bounds are equal).
(string Core, string AspNetCore) ReadRuntimePackVersions()
{
    var assetsPath = "src/OpenList.Server/obj/project.assets.json";
    if (!System.IO.File.Exists(assetsPath))
        throw new InvalidOperationException(
            $"{assetsPath} not found — the runtime pack restore must run before the layout is staged.");

    string core = "", asp = "";
    using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(assetsPath));
    if (!doc.RootElement.TryGetProperty("project", out var projectEl) ||
        !projectEl.TryGetProperty("frameworks", out var frameworksEl))
        throw new InvalidOperationException($"{assetsPath} has no project.frameworks section.");

    foreach (var framework in frameworksEl.EnumerateObject())
    {
        if (!framework.Value.TryGetProperty("downloadDependencies", out var deps)) continue;
        foreach (var dep in deps.EnumerateArray())
        {
            var name = dep.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var ver  = dep.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            // "[10.0.14, 10.0.14]" → "10.0.14"
            ver = ver.Trim('[', ']').Split(',')[0].Trim();
            if (string.IsNullOrEmpty(ver)) continue;
            if (name == $"Microsoft.NETCore.App.Runtime.{rid}") core = ver;
            if (name == $"Microsoft.AspNetCore.App.Runtime.{rid}") asp  = ver;
        }
    }

    if (string.IsNullOrEmpty(core) || string.IsNullOrEmpty(asp))
        throw new InvalidOperationException(
            $"Could not resolve runtime pack versions for '{rid}' from {assetsPath} " +
            $"(core='{core}', aspnetcore='{asp}'). Is '{rid}' a valid RID?");
    return (core, asp);
}

// Copy a runtime pack's `runtimes/<rid>` payload into the install layout:
//   *hostfxr* goes to host/fxr/<ver>/, everything else (native + managed) to the shared
//   framework folder, which is exactly how a real .NET install is laid out.
void CopyRuntimePack(string packRoot, string ridPath, string frameworkName, string version, string destDir)
{
    var sharedDir = System.IO.Path.Combine(destDir, "shared", frameworkName, version);
    var fxrDir    = System.IO.Path.Combine(destDir, "host", "fxr", version);
    System.IO.Directory.CreateDirectory(sharedDir);

    var nativeDir = System.IO.Path.Combine(packRoot, ridPath, "native");
    if (System.IO.Directory.Exists(nativeDir))
    {
        foreach (var file in System.IO.Directory.GetFiles(nativeDir))
        {
            var name = System.IO.Path.GetFileName(file);
            // hostfxr.dll / libhostfxr.so / libhostfxr.dylib — the loader itself, not a framework
            // assembly. hostpolicy.* deliberately stays in the shared framework folder.
            if (name.StartsWith("hostfxr.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("libhostfxr.", StringComparison.OrdinalIgnoreCase))
            {
                System.IO.Directory.CreateDirectory(fxrDir);
                System.IO.File.Copy(file, System.IO.Path.Combine(fxrDir, name), overwrite: true);
            }
            else
            {
                System.IO.File.Copy(file, System.IO.Path.Combine(sharedDir, name), overwrite: true);
            }
        }
    }

    var libRoot = System.IO.Path.Combine(packRoot, ridPath, "lib");
    if (System.IO.Directory.Exists(libRoot))
    {
        foreach (var libDir in System.IO.Directory.GetDirectories(libRoot))
            foreach (var file in System.IO.Directory.GetFiles(libDir))
                System.IO.File.Copy(file, System.IO.Path.Combine(sharedDir, System.IO.Path.GetFileName(file)),
                                    overwrite: true);
    }
}

// Stage the single .NET install layout that every apphost in the bundle points at.
void BuildRuntimeRoot(string destDir)
{
    if (string.IsNullOrEmpty(rid))
        throw new InvalidOperationException(
            "The shared runtime root needs --rid=<rid> — a framework-dependent bundle uses the " +
            "machine-wide .NET runtime and must not ship one.");

    Information($"Staging shared .NET runtime root for {rid} → {destDir}");

    // Self-contained restore: it is what puts Microsoft.NETCore.App.Runtime.<rid> (and the
    // ASP.NET Core one) into the package cache, and its assets file is the authoritative
    // record of which versions the SDK picked for this RID.
    DotNetRestore(ServerProject, new DotNetRestoreSettings
    {
        MSBuildSettings = new DotNetMSBuildSettings()
            .WithProperty("RuntimeIdentifier", rid)
            .WithProperty("SelfContained", "true"),
    });

    var (coreVer, aspVer) = ReadRuntimePackVersions();
    Information($"Runtime packs: Microsoft.NETCore.App {coreVer}, Microsoft.AspNetCore.App {aspVer}");

    var nuget = NuGetPackagesRoot();
    var corePack = System.IO.Path.Combine(nuget, $"microsoft.netcore.app.runtime.{rid}", coreVer);
    var aspPack  = System.IO.Path.Combine(nuget, $"microsoft.aspnetcore.app.runtime.{rid}", aspVer);
    foreach (var (pack, label) in new[] { (corePack, "Microsoft.NETCore.App"), (aspPack, "Microsoft.AspNetCore.App") })
        if (!System.IO.Directory.Exists(pack))
            throw new InvalidOperationException(
                $"Runtime pack for {label}/{rid} not found at {pack} — restore did not download it.");

    if (System.IO.Directory.Exists(destDir)) CleanDirectory(destDir);
    System.IO.Directory.CreateDirectory(destDir);

    // The RID sub-folder inside the pack is the *pack* RID (e.g. linux-musl-x64), same string.
    CopyRuntimePack(corePack, $"runtimes/{rid}", "Microsoft.NETCore.App", coreVer, destDir);
    CopyRuntimePack(aspPack,  $"runtimes/{rid}", "Microsoft.AspNetCore.App", aspVer, destDir);

    var files = System.IO.Directory.GetFiles(destDir, "*", System.IO.SearchOption.AllDirectories).Length;
    Information($"Shared runtime root staged: {files} files under {destDir}");
}

// Resolve the latest non-prerelease frontend tag from the upstream GitHub API.
// Only used by the `--frontend-version=latest` legacy path; normal builds never hit the network.
string ResolveLatestFrontendVersion()
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "OpenList.Net-Cake");
    var json = http.GetStringAsync($"https://api.github.com/repos/{UpstreamRepo}/releases/latest")
                   .GetAwaiter().GetResult();
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.GetProperty("tag_name").GetString()
        ?? throw new InvalidOperationException("releases/latest returned no tag_name");
}

// Download the non-lite dist tarball and flatten it so dest/ directly contains index.html.
void FetchFrontend(string ver, string dest)
{
    if (DirectoryExists(dest)) CleanDirectory(dest);
    EnsureDirectoryExists(dest);

    var url  = $"https://github.com/{UpstreamRepo}/releases/download/{ver}/openlist-frontend-dist-{ver}.tar.gz";
    var tgz  = $"{output}/frontend-dist.tar.gz";
    Information($"Downloading {url}");
    DownloadFile(url, tgz);

    var tmp = $"{output}/.fe-extract";
    if (DirectoryExists(tmp)) CleanDirectory(tmp);
    EnsureDirectoryExists(tmp);
    StartProcess("tar", $"-xzf \"{tgz}\" -C \"{tmp}\"");

    var index = GetFiles($"{tmp}/**/index.html").FirstOrDefault()
        ?? throw new InvalidOperationException("index.html not found in frontend tarball");
    var distRoot = index.GetDirectory().FullPath;
    Information($"Flattening frontend dist from {distRoot}");
    CopyTree(distRoot, dest);

    System.IO.File.WriteAllText(System.IO.Path.Combine(dest, "VERSION"), ver);
}

// Emit platform-appropriate launchers that fix OpenList__DistDir for the bundle layout
// (app/ + frontend/dist/). The published appsettings.json sets DistDir="../../frontend"
// which is only correct for dev runs from src/OpenList.Server/.
void WriteRunScripts(string bundleRoot)
{
    var isWindowsRid = !string.IsNullOrEmpty(rid) && rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);
    var isFdd = string.IsNullOrEmpty(rid);
    const string distDir = "../frontend/dist";

    if (!isWindowsRid) // unix RIDs and framework-dependent
    {
        var unixCmd = isFdd ? "dotnet OpenList.Server.dll" : "./OpenList.Server";
        var shPath = System.IO.Path.Combine(bundleRoot, "run.sh");
        System.IO.File.WriteAllLines(shPath, new[]
        {
            "#!/usr/bin/env sh",
            "cd \"$(dirname \"$0\")/app\" || exit 1",
            $"export OpenList__DistDir=\"{distDir}\"",
            // Kestrel 的监听地址只认 ASPNETCORE_URLS（appsettings 里的 Scheme.HttpPort 仅参与
            // 校验）—— 不设它就会退到 Kestrel 默认的 5000，与 README 承诺的 5244 不符。
            ": \"${ASPNETCORE_URLS:=http://0.0.0.0:5244}\"",
            "export ASPNETCORE_URLS",
            $"exec {unixCmd}",
        });
        if (!IsRunningOnWindows())
            StartProcess("chmod", $"+x \"{shPath}\"");
    }

    if (isWindowsRid || isFdd)
    {
        var winCmd = isFdd ? "dotnet OpenList.Server.dll" : "OpenList.Server.exe";
        System.IO.File.WriteAllLines(System.IO.Path.Combine(bundleRoot, "run.bat"), new[]
        {
            "@echo off",
            "cd /d \"%~dp0app\"",
            $"set OpenList__DistDir={distDir}",
            "if not defined ASPNETCORE_URLS set ASPNETCORE_URLS=http://0.0.0.0:5244",
            winCmd,
        });
    }
}

void WriteReadme(string bundleRoot)
{
    var name = BundleNameVersioned();
    var isFdd = string.IsNullOrEmpty(rid);
    var runUnix = isFdd ? "sh run.sh" : "./run.sh";

    // RID bundles carry one shared runtime and one exe per folder; the portable one deliberately
    // carries neither and is started through `dotnet` from the host.
    var layout = new[]
    {
        "Layout:",
        isFdd
            ? "  app/             .NET server (publish output, started via `dotnet`)"
            : "  app/             OpenList.Server (single exe) + appsettings.json",
        isFdd
            ? "  cli/             Ops CLI (openlist) — password recovery, storage admin"
            : "  cli/             openlist (single exe) — password recovery, storage admin",
    }
    .Concat(isFdd ? Array.Empty<string>() : new[] { "  dotnet/          Shared .NET runtime — used by app/ and cli/" })
    .Concat(new[]
    {
        "  frontend/dist/   OpenList SPA (Vue 3, built from this repo)",
        "  run.sh / run.bat Launcher that sets OpenList__DistDir and starts the server",
        "  README.txt       This file",
        "",
        "Run:",
        "  Linux/macOS:  " + runUnix,
        "  Windows:      run.bat",
        "",
    })
    .Concat(isFdd ? Array.Empty<string>() : new[]
    {
        "No .NET runtime has to be installed on the host: the executables find the one in",
        "dotnet/ through a relative path embedded in them (AppHostRelativeDotNet). Keep that",
        "folder next to app/ — moving app/ on its own will make it fail to start.",
        "",
    })
    .Concat(new[]
    {
        "The launcher exports OpenList__DistDir=../frontend/dist (relative to app/) so the",
        "server finds the SPA without editing appsettings.json. Default port: http://0.0.0.0:5244",
        "",
        "Lost the admin password? The built-in admin cannot be deleted, renamed or demoted,",
        "so cli/ is the way back in — it talks to the database directly and works even when",
        "the server is not running:",
        "  cli/openlist admin info        show the admin username",
        "  cli/openlist admin random      reset to a random password (printed to console)",
        "  cli/openlist admin set PASS    set a specific password",
        "",
        "Data is stored under app/data/ by default. Configure via OpenList__ environment variables",
        "(e.g. OpenList__Database__Type=postgres).",
    });

    System.IO.File.WriteAllLines(
        System.IO.Path.Combine(bundleRoot, "README.txt"),
        new[] { name, "================", "" }.Concat(layout));
}

// Desktop bundle README: the GUI is the entry point, the server is a managed child
// process sitting right next to it in the bundle root.
void WriteDesktopReadme(string bundleRoot)
{
    var name = BundleNameVersioned();
    var isWindowsRid = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);
    var exeName = isWindowsRid ? "OpenList.Desktop.exe" : "OpenList.Desktop";
    var coreName = isWindowsRid ? "OpenList.Server.exe" : "OpenList.Server";
    var cliLabel = isWindowsRid ? "openlist.exe" : "openlist";
    var runtimeLabel = RuntimeRootName + "/";
    var feLabel = "frontend/dist/";
    System.IO.File.WriteAllLines(System.IO.Path.Combine(bundleRoot, "README.txt"), new[]
    {
        $"{name}",
        "================",
        "",
        "Layout — only the three executables live in this directory:",
        $"  {exeName,-26} Avalonia desktop control app (start this)",
        $"  {coreName,-26} Server core (managed child process)",
        $"  {cliLabel,-26} Ops CLI — password recovery, storage admin",
        "",
        "Directories:",
        $"  {runtimeLabel,-26} Shared .NET runtime — used by all three exes",
        $"  {feLabel,-26} OpenList SPA (Vue 3, built from this repo)",
        $"  {"config/",-26} Editable settings (appsettings.json)",
        "",
        "Each executable is single-file and framework-dependent: the assemblies it needs are",
        "inside the exe, and the .NET runtime is the one shared copy in",
        $"{RuntimeRootName}/ (found through AppHostRelativeDotNet). Keep the folders in place —",
        "copying a bare exe elsewhere will not run.",
        "",
        "Run:",
        $"  Start {exeName} from a terminal or double-click it (Windows).",
        "  The desktop app launches the server automatically (port configurable in settings),",
        "  shows the tray icon and live log viewer. Data is stored under data/ by default",
        "(configurable in settings).",
        "",
        "Port, database and log paths live in config/appsettings.json (the desktop settings",
        "page writes them too). Remove the file and the built-in defaults apply.",
        "",
        "The desktop app injects OpenList__DistDir to point the server at frontend/dist,",
        "so the web UI is served on the same port as the API. Default port: 5244.",
        "",
        "Note (macOS/Linux): the desktop app is not wrapped in an .app/.desktop bundle —",
        "run it directly from this directory.",
    });
}

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Description("Remove previous build artifacts")
    .Does(() =>
{
    if (DirectoryExists(output))
        CleanDirectory(output);
});

Task("Format")
    .Description("dotnet format (lint/style) — not wired into Default/CI")
    .Does(() =>
{
    var exit = StartProcess("dotnet", $"format {SolutionFile}");
    if (exit != 0)
        throw new InvalidOperationException($"dotnet format exited with {exit}");
});

// Compute the version. If --semver is passed it is used as-is (CI mode: the workflow runs
// `dotnet gitversion` itself and forwards the result via --semver). Otherwise the task
// restores the local GitVersion tool and runs it.
Task("Version")
    .Description("Compute semantic version via GitVersion (or honor --semver=<x.y.z>)")
    .Does(() =>
{
    if (!string.IsNullOrEmpty(_semver))
    {
        // Caller pre-injected a semver via --semver=. Derive deterministic assembly/file versions
        // and informational version so MSBuild gets consistent inputs.
        var baseVer = _semver.Split('+')[0];
        var parts = baseVer.Split('-')[0].Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var ma) ? ma : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var pa) ? pa : 0;
        _assemblyVer = $"{major}.{minor}.{patch}.0";
        _fileVer     = _assemblyVer;
        _fullSemVer  = _semver;
        _infoVer     = _semver;
        Information($"Using injected semver={_semver} (AssemblyVer={_assemblyVer})");
        return;
    }

    ComputeGitVersion();
});

Task("Restore")
    .Description("dotnet restore")
    .Does(() =>
{
    DotNetRestore(SolutionFile);
});

Task("Build")
    .Description($"dotnet build ({configuration})")
    .IsDependentOn("Restore")
    .Does(() =>
{
    // Version props are applied only when Version has already run (e.g. via Publish or explicit
    // --target=Version). For local dev (Default = Restore+Build+Test) this is a no-op so the
    // build does not require GitVersion to be installed.
    DotNetBuild(SolutionFile, new DotNetBuildSettings
    {
        Configuration = configuration,
        NoRestore = true,
        MSBuildSettings = WithVersionProps(new DotNetMSBuildSettings()),
    });
});

Task("Test")
    .Description("dotnet test (MTP mode, opted in via global.json)")
    .IsDependentOn("Build")
    .Does(() =>
{
    // .NET 10 + Microsoft.Testing.Platform requires the new CLI shape — the test
    // project is addressed with `--solution` rather than a positional argument.
    // Cake's DotNetTest alias still emits the legacy VSTest form, so call the CLI
    // directly (same approach as the Format target below).
    var exit = StartProcess("dotnet", $"test --solution {SolutionFile} -c {configuration} --no-build");
    if (exit != 0)
        throw new InvalidOperationException($"dotnet test exited with {exit}");
});

Task("Frontend")
    .Description("Stage the SPA dist (default: frontend/dist, built from this repo)")
    .Does(() =>
{
    var dest = FeDistDir();
    if (FileExists($"{dest}/index.html"))
    {
        Information($"Using frontend dist '{dest}'.");
        return;
    }

    // Legacy escape hatch: bundle an upstream OpenList-Frontend release instead of ours.
    // Kept because it is the only way to A/B a regression against the old SPA, but it is
    // never the default — silently downloading someone else's dist is how the bundle ends
    // up serving a frontend that does not match the backend it ships with.
    if (!string.IsNullOrEmpty(frontendVer))
    {
        var ver = frontendVer == "latest" ? ResolveLatestFrontendVersion() : frontendVer;
        Information($"'{dest}/index.html' missing — downloading upstream frontend release {ver}.");
        FetchFrontend(ver, dest);
        return;
    }

    throw new InvalidOperationException(
        $"'{dest}/index.html' not found. Build the SPA first:\n" +
        "  pnpm -C frontend install && pnpm -C frontend build\n" +
        "(or pass --frontend-version=<upstream tag> to bundle an upstream OpenList-Frontend release)");
});

// Publish one app as "RID-specific framework-dependent": the apphost is produced, but the
// runtime is not copied — the exe consumes the bundle's single shared `dotnet/` root instead.
//
// appRelativeDotNet is the path from the apphost to that root (e.g. `dotnet` for the desktop
// exe at the bundle root, `../dotnet` for server/ and cli/). It is embedded into the apphost at
// publish time only (the SDK rewrites the apphost on publish, not on build), so the inner dev
// loop — `dotnet run`, F5, bin/Debug — keeps resolving the machine-wide runtime as before.
//
// Without --rid there is no apphost at all (UseAppHost=false ⇒ `dotnet X.dll`), so nothing is
// embedded and the global runtime is used; that path is unchanged.
void PublishApp(string project, string publishDir, string appRelativeDotNet, bool singleFile = true)
{
    var msbuild = new DotNetMSBuildSettings()
        .WithProperty("SelfContained", "false")
        // NuGet's up-to-date check has bitten this repo: after a no-RID publish, a RID publish
        // in the same working tree can keep the no-RID assets file (no `.../win-x64` target),
        // and the compile then fails with CS0246 for whole packages (`WebDav` in
        // OpenList.Drivers). Forcing restore costs a couple of seconds and removes the class.
        .WithProperty("RestoreForce", "true");

    if (string.IsNullOrEmpty(rid))
    {
        msbuild.WithProperty("UseAppHost", "false");
    }
    else
    {
        msbuild.WithProperty("RuntimeIdentifier", rid);
        if (!string.IsNullOrEmpty(appRelativeDotNet))
        {
            msbuild.WithProperty("AppHostDotNetSearch", "AppRelative")
                   .WithProperty("AppHostRelativeDotNet", appRelativeDotNet);
        }

        if (singleFile)
        {
            // 单文件 + 框架依赖：托管程序集（含 Avalonia / SkiaSharp 的托管半边）和 native 库
            // 全部捆进 exe，运行时仍然只用 bundle 里那一份 dotnet/ —— 于是发布目录里只剩
            // 一个可执行文件，桌面包的根目录才可能"只有三个 exe"。
            //
            // IncludeNativeLibrariesForSelfExtract 是必须的：不加的话 libSkiaSharp / libHarfBuzz
            // 会以独立 .dll 落在 exe 旁边（同样是根目录污染），加了才会打进 bundle 并在首次
            // 运行时解压到 %TEMP%\.net\<app>\<hash>\。
            msbuild.WithProperty("PublishSingleFile", "true")
                   .WithProperty("IncludeNativeLibrariesForSelfExtract", "true")
                   // 调试符号默认以 .pdb 落在 exe 旁边 —— 那又是根目录里的散文件。
                   .WithProperty("DebugType", "none")
                   .WithProperty("DebugSymbols", "false");
        }
    }
    WithVersionProps(msbuild);

    // 先清空——`dotnet publish` 到固定目录是增量复制：它只覆盖"有变化"的文件，
    // 从不删除上一次发布留下的东西。于是同一个 artifacts/publish 目录里一旦跑过
    // 一次自包含发布（历史上的默认），之后所有发布都会把整套运行时装进包里：
    // 实测 415 个文件里 330+ 个是几个月前的 coreclr / System.Private.CoreLib，
    // 一个本该 ~40 MB 的包被撑到 166 MB，而且这些文件不属于本次构建。
    // （CI 每次都是干净工作区，所以这个问题只在本地反复构建时暴露。）
    if (DirectoryExists(publishDir)) CleanDirectory(publishDir);

    DotNetPublish(project, new DotNetPublishSettings
    {
        Configuration = configuration,
        OutputDirectory = publishDir,
        MSBuildSettings = msbuild,
    });
}

// Where the shared runtime root sits relative to each apphost in this bundle kind:
//   desktop bundle — all three exes share the bundle root, so everybody says `dotnet`
//   server bundle  — server and cli live in app/ and cli/, i.e. one level down
string BundleAppRelativeDotNet() => IsDesktopApp() ? RuntimeRootName : $"../{RuntimeRootName}";

// `.exe` only for Windows RIDs — the bundle is staged on any host, so this cannot ask the OS.
string ExeSuffix() => rid.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? ".exe" : "";

// Server publish. In the desktop bundle it lands *next to* the GUI (OpenListCoreService
// probes `<base>/OpenList.Server.exe` first); in the headless bundle it goes into app/.
void PublishServer()
{
    var publishDir = $"{output}/publish";
    PublishApp(ServerProject, publishDir, BundleAppRelativeDotNet());

    // Drop a VERSION file alongside the assembly so deployments can surface build metadata
    // (matching the frontend's frontend/dist/VERSION convention).
    WriteVersionMetadata(publishDir);
}

// CLI publish: the ops CLI (admin / storage / cancel2fa) ships next to the server so
// password recovery works without the web layer. It shares the very same `dotnet/` root —
// a locked-out admin still does not have to install a runtime first.
void PublishCli()
{
    PublishApp(CliProject, $"{output}/publish-cli", BundleAppRelativeDotNet());
}

// Desktop publish: Avalonia GUI at the bundle root, so its runtime root is `dotnet/` itself.
void PublishDesktop()
{
    if (string.IsNullOrEmpty(rid))
        throw new InvalidOperationException(
            "Desktop publish requires --rid=<rid> — the GUI bundle needs a runtime, and the " +
            "shared runtime root is built per RID.");

    var publishDir = $"{output}/publish-desktop";
    PublishApp(DesktopProject, publishDir, RuntimeRootName);
    WriteVersionMetadata(publishDir);
}

Task("Publish")
    .Description("dotnet publish — server: framework-dependent unless --rid; desktop: requires --rid")
    .IsDependentOn("Version")
    .Does(() =>
{
    if (IsDesktopApp())
    {
        // 桌面包 = GUI + 服务端 + CLI 全在 bundle 根（三个 exe 同目录，共用 dotnet/）。
        // Bundle 的桌面分支三个都要拷，所以这里必须都发布：只发 GUI 的话
        // `--app=desktop --target=Bundle/Package` 会在拷 `publish/` 时直接抛
        // DirectoryNotFoundException。
        // （CI 不走这条路 —— 它在 workflow 里分别 publish 三次，所以这个洞一直没被发现。）
        PublishDesktop();
        PublishServer();
    }
    else
    {
        PublishServer();
    }

    // The apps are framework-dependent now, so the bundle is only runnable with the shared
    // runtime root next to them — build it as part of Publish, not as an afterthought.
    // (No --rid ⇒ portable bundle: no apphost, no runtime, the host's .NET is used.)
    if (!string.IsNullOrEmpty(rid))
        BuildRuntimeRoot($"{output}/{RuntimeRootName}");
});

// CLI ships with the server bundle. It is the recovery path for a locked-out admin, so it
// must always be produced — including for framework-dependent builds (the same .NET runtime
// the server already needs is present, so no extra runtime burden there).
Task("PublishCli")
    .Description("dotnet publish — ops CLI (admin / storage / cancel2fa)")
    .IsDependentOn("Version")
    .Does(() => PublishCli());

// The shared runtime root on its own. CI calls this once per matrix leg: it isolates the
// only piece that needs NuGet's runtime packs, so the workflow does not have to re-implement
// the pack→install-layout mapping in bash.
Task("Runtime")
    .Description("Stage the shared .NET install layout (dotnet/) for --rid into --dest")
    .Does(() =>
{
    var target = string.IsNullOrEmpty(dest) ? $"{output}/{RuntimeRootName}" : $"{dest}/{RuntimeRootName}";
    BuildRuntimeRoot(target);
});

Task("Bundle")
    .Description("Stage publish output + frontend dist + launchers + README into a release layout")
    .IsDependentOn("Frontend")
    .IsDependentOn("Publish")
    .IsDependentOn("PublishCli")
    .Does(() =>
{
    var name = BundleNameVersioned();
    var bundleRoot = $"{output}/bundle/{name}";
    if (DirectoryExists(bundleRoot)) CleanDirectory(bundleRoot);

    if (IsDesktopApp())
    {
        // Desktop layout — the bundle root holds nothing but the three executables:
        //
        //   OpenList.Desktop.exe   Avalonia GUI (start this)
        //   OpenList.Server.exe    server core (managed child process)
        //   openlist.exe           ops CLI (password recovery, storage admin)
        //   appsettings.json       the one file you may want to edit by hand
        //   dotnet/                ONE shared .NET runtime for all three
        //   frontend/dist/         the SPA
        //
        // Every exe is a single-file framework-dependent apphost: the managed assemblies and the
        // native libraries live inside the exe, so nothing else has to sit next to it. Before
        // this, merging the three publish outputs dropped ~150 loose .dll/.pdb files in the root.
        if (string.IsNullOrEmpty(rid))
            throw new InvalidOperationException("Desktop bundle requires --rid=<rid>.");
        EnsureDirectoryExists(bundleRoot);
        EnsureDirectoryExists($"{bundleRoot}/frontend/dist");

        var suffix = ExeSuffix();
        CopyFile($"{output}/publish/OpenList.Server{suffix}", $"{bundleRoot}/OpenList.Server{suffix}");
        CopyFile($"{output}/publish-desktop/OpenList.Desktop{suffix}", $"{bundleRoot}/OpenList.Desktop{suffix}");
        CopyFile($"{output}/publish-cli/openlist{suffix}", $"{bundleRoot}/openlist{suffix}");
        // appsettings.json 进 config/：桌面包根目录只允许有可执行文件，而配置又要可编辑
        // （端口、数据库、日志路径）。服务器在启动时会额外读 <exe>/config/appsettings.json。
        EnsureDirectoryExists($"{bundleRoot}/config");
        CopyFile($"{output}/publish/appsettings.json", $"{bundleRoot}/config/appsettings.json");

        CopyTree(FeDistDir(), $"{bundleRoot}/frontend/dist");
        // All three apphosts say AppHostRelativeDotNet=dotnet — the root is right here.
        CopyTree($"{output}/{RuntimeRootName}", $"{bundleRoot}/{RuntimeRootName}");

        WriteDesktopReadme(bundleRoot);
    }
    else
    {
        EnsureDirectoryExists($"{bundleRoot}/app");
        EnsureDirectoryExists($"{bundleRoot}/cli");
        EnsureDirectoryExists($"{bundleRoot}/frontend/dist");

        if (string.IsNullOrEmpty(rid))
        {
            // Portable bundle: no apphost at all, so app/ is the raw publish output.
            CopyTree($"{output}/publish", $"{bundleRoot}/app");
            CopyTree($"{output}/publish-cli", $"{bundleRoot}/cli");
        }
        else
        {
            // Same single-file treatment as the desktop bundle: one exe per folder.
            var suffix = ExeSuffix();
            CopyFile($"{output}/publish/OpenList.Server{suffix}", $"{bundleRoot}/app/OpenList.Server{suffix}");
            CopyFile($"{output}/publish/appsettings.json", $"{bundleRoot}/app/appsettings.json");
            CopyFile($"{output}/publish/VERSION", $"{bundleRoot}/app/VERSION");
            CopyFile($"{output}/publish-cli/openlist{suffix}", $"{bundleRoot}/cli/openlist{suffix}");
            // Same sharing for the headless bundle: app/ + cli/ → `../dotnet`.
            CopyTree($"{output}/{RuntimeRootName}", $"{bundleRoot}/{RuntimeRootName}");
        }

        CopyTree(FeDistDir(), $"{bundleRoot}/frontend/dist");

        WriteRunScripts(bundleRoot);
        WriteReadme(bundleRoot);
    }
    Information($"Bundle staged at {bundleRoot}");
});

Task("Package")
    .Description("Package the bundle (--archive=zip|tar.gz, default: zip)")
    .IsDependentOn("Bundle")
    .Does(() =>
{
    var name = BundleNameVersioned();
    var bundleRoot = $"{output}/bundle/{name}";
    var ext = archive == "tar.gz" ? "tar.gz" : "zip";
    var archivePath = $"{output}/{name}.{ext}";

    if (archive == "tar.gz")
    {
        StartProcess("tar", $"-czf \"{archivePath}\" -C \"{output}/bundle\" \"{name}\"");
    }
    else
    {
        Zip(bundleRoot, archivePath);
    }
    Information($"Package: {archivePath}");
});

Task("CI")
    .Description("Restore + Build + Test + Frontend + Bundle (framework-dependent) — local CI-equivalent; ci.yml uses dotnet CLI directly")
    .IsDependentOn("Bundle");

Task("Default")
    .Description("Restore + Build + Test (local default)")
    .IsDependentOn("Test");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////
RunTarget(target);