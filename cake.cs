#!/usr/bin/env dotnet
#:sdk Cake.Sdk@6.2.0
// Cake build script for OpenList.Net.
//
// Run a task:
//   dotnet cake.cs --target=Test
//   dotnet cake.cs --target=Format
//   dotnet cake.cs --target=Package --rid=linux-x64 --archive=tar.gz
//   dotnet cake.cs --target=CI                                   # framework-dependent bundle
//   dotnet cake.cs --target=Bundle --frontend-dist=frontend      # use committed frontend, skip fetch
//
// Arguments:
//   --configuration=Release    MSBuild configuration
//   --output=artifacts         Artifact root
//   --rid=<rid>                Target runtime; empty = framework-dependent
//   --self-contained=true      Self-contained publish (only meaningful with --rid)
//   --frontend-dist=<path>     Frontend dist source (skip upstream fetch if it exists)
//   --frontend-version=<tag>   Pin upstream frontend tag (default: latest release)
//   --archive=zip|tar.gz       Package format (Package target)

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

const string SolutionFile  = "OpenList.slnx";
const string ServerProject = "src/OpenList.Server/OpenList.Server.csproj";
const string UpstreamRepo  = "OpenListTeam/OpenList-Frontend";

// When --frontend-dist is given, use it; otherwise stage the upstream fetch under <output>/frontend-dist.
string FeDistDir() => string.IsNullOrEmpty(frontendDist) ? $"{output}/frontend-dist" : frontendDist;

string BundleName() => string.IsNullOrEmpty(rid)
    ? "openlist-net-framework-dependent"
    : $"openlist-net-{rid}";

//////////////////////////////////////////////////////////////////////
// SETUP
//////////////////////////////////////////////////////////////////////
Setup(_ =>
{
    Information($"Target={target} Configuration={configuration} Output={output}");
    Information($"Rid={(string.IsNullOrEmpty(rid) ? "<framework-dependent>" : rid)} SelfContained={selfContained}");
});

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
    var name = BundleName();
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
    DotNetBuild(SolutionFile, new DotNetBuildSettings
    {
        Configuration = configuration,
        NoRestore = true,
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

    DotNetPublish(ServerProject, new DotNetPublishSettings
    {
        Configuration = configuration,
        OutputDirectory = $"{output}/publish",
        MSBuildSettings = msbuild,
    });
});

Task("Bundle")
    .Description("Stage publish output + frontend dist + launchers + README into a release layout")
    .IsDependentOn("Frontend")
    .IsDependentOn("Publish")
    .Does(() =>
{
    var bundleRoot = $"{output}/bundle/{BundleName()}";
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
    var name = BundleName();
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
