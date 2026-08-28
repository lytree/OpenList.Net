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
//
// Arguments:
//   --configuration=Release    MSBuild configuration
//   --output=artifacts         Artifact root
//   --rid=<rid>                Target runtime; empty = framework-dependent
//   --self-contained=true      Self-contained publish (only meaningful with --rid)
//   --frontend-dist=<path>     Frontend dist source (skip upstream fetch if it exists)
//   --frontend-version=<tag>   Pin upstream frontend tag (default: latest release)
//   --archive=zip|tar.gz       Package format (Package target)
//   --semver=<x.y.z>           Pre-computed semver (skips local GitVersion; CI uses this)

using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

var target        = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var output        = Argument("output", "artifacts");
var rid           = Argument("rid", "");
var selfContained = Argument<bool>("self-contained", false);
var frontendDist  = Argument("frontend-dist", "");
var frontendVer   = Argument("frontend-version", "");
var archive       = Argument("archive", "zip");
// Allow callers to inject a pre-computed semver (e.g. CI workflow that runs GitVersion itself).
// When empty, the Version task computes it locally via `dotnet gitversion`.
var semverArg     = Argument("semver", "");

const string SolutionFile  = "OpenList.slnx";
const string ServerProject = "src/OpenList.Server/OpenList.Server.csproj";
const string UpstreamRepo  = "OpenListTeam/OpenList-Frontend";

// When --frontend-dist is given, use it; otherwise stage the upstream fetch under <output>/frontend-dist.
string FeDistDir() => string.IsNullOrEmpty(frontendDist) ? $"{output}/frontend-dist" : frontendDist;

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
string BundleName() => string.IsNullOrEmpty(rid)
    ? "openlist-net-framework-dependent"
    : $"openlist-net-{rid}";

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
    Information($"Target={target} Configuration={configuration} Output={output}");
    Information($"Rid={(string.IsNullOrEmpty(rid) ? "<framework-dependent>" : rid)} SelfContained={selfContained}");
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

// Resolve the latest non-prerelease frontend tag from the upstream GitHub API.
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
            winCmd,
        });
    }
}

void WriteReadme(string bundleRoot)
{
    var name = BundleNameVersioned();
    var isFdd = string.IsNullOrEmpty(rid);
    var runUnix = isFdd ? "sh run.sh" : "./run.sh";
    System.IO.File.WriteAllLines(System.IO.Path.Combine(bundleRoot, "README.txt"), new[]
    {
        $"{name}",
        "================",
        "",
        "Layout:",
        "  app/            .NET server (publish output)",
        "  frontend/dist/   OpenList SPA (pre-built, upstream non-lite release)",
        "  run.sh / run.bat Launcher that sets OpenList__DistDir and starts the server",
        "  README.txt       This file",
        "",
        "Run:",
        "  Linux/macOS:  " + runUnix,
        "  Windows:      run.bat",
        "",
        "The launcher exports OpenList__DistDir=../frontend/dist (relative to app/) so the",
        "server finds the SPA without editing appsettings.json. Default port: http://0.0.0.0:5244",
        "",
        "Data is stored under app/data/ by default. Configure via OpenList__ environment variables",
        "(e.g. OpenList__Database__Type=postgres).",
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
    .Description("dotnet test")
    .IsDependentOn("Build")
    .Does(() =>
{
    DotNetTest(SolutionFile, new DotNetTestSettings
    {
        Configuration = configuration,
        NoBuild = true,
    });
});

Task("Frontend")
    .Description("Fetch the latest non-lite OpenList-Frontend dist from upstream GitHub releases")
    .Does(() =>
{
    // Skip the upstream fetch when an explicit, existing frontend-dist is provided.
    if (!string.IsNullOrEmpty(frontendDist) && DirectoryExists(frontendDist))
    {
        Information($"Using explicit frontend-dist='{frontendDist}' (skipping upstream fetch).");
        return;
    }

    var ver = string.IsNullOrEmpty(frontendVer) ? ResolveLatestFrontendVersion() : frontendVer;
    Information($"Frontend version: {ver}");
    FetchFrontend(ver, FeDistDir());
});

Task("Publish")
    .Description("dotnet publish — framework-dependent unless --rid is set")
    .IsDependentOn("Version")
    .Does(() =>
{
    var msbuild = new DotNetMSBuildSettings();
    if (!string.IsNullOrEmpty(rid))
    {
        msbuild.WithProperty("PublishSingleFile", "true")
               .WithProperty("SelfContained", "true")
               .WithProperty("RuntimeIdentifier", rid)
               .WithProperty("IncludeNativeLibrariesForSelfExtract", "true")
               .WithProperty("EnableCompressionInSingleFile", "true");
    }
    else
    {
        msbuild.WithProperty("SelfContained", "false")
               .WithProperty("UseAppHost", "false");
    }
    WithVersionProps(msbuild);

    var publishDir = $"{output}/publish";
    DotNetPublish(ServerProject, new DotNetPublishSettings
    {
        Configuration = configuration,
        OutputDirectory = publishDir,
        MSBuildSettings = msbuild,
    });

    // Drop a VERSION file alongside the assembly so deployments can surface build metadata
    // (matching the frontend's frontend/dist/VERSION convention).
    WriteVersionMetadata(publishDir);
});

Task("Bundle")
    .Description("Stage publish output + frontend dist + launchers + README into a release layout")
    .IsDependentOn("Frontend")
    .IsDependentOn("Publish")
    .Does(() =>
{
    var name = BundleNameVersioned();
    var bundleRoot = $"{output}/bundle/{name}";
    if (DirectoryExists(bundleRoot)) CleanDirectory(bundleRoot);
    EnsureDirectoryExists($"{bundleRoot}/app");
    EnsureDirectoryExists($"{bundleRoot}/frontend/dist");

    CopyTree($"{output}/publish", $"{bundleRoot}/app");
    CopyTree(FeDistDir(), $"{bundleRoot}/frontend/dist");

    WriteRunScripts(bundleRoot);
    WriteReadme(bundleRoot);
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