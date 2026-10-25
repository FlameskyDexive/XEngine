# This file is part of the XEngine Game Engine
# Licensed under the MIT License. See the LICENSE file in the project root for details.

<#
.SYNOPSIS
    Fabricates an XEngine OpenHarmony SDK Bundle archive from locally available inputs:
    the OpenHarmony.NET.HapTooling NuGet package (Stage template + SDK catalog) and the six
    OpenHarmony.NET feed packages from the local NuGet cache.

.DESCRIPTION
    The provider Bundle contract (HarmonyOSSdkBundleImporter) requires a .zip containing
    release-manifest.json plus: SHA256SUMS, sbom.spdx.json, schemas/sdk-catalog.json,
    templates/stage-player/*, licenses/*, and feed/{id}.{version}.nupkg for exactly the six
    required package ids. This script assembles those from the local NuGet cache so a machine
    can vendor a Bundle without provider-supplied archives. The sdk-catalog.json pins official
    SDK oh-uni-package.json hashes; HarmonyOSToolchainResolver validates the local SDK against
    it at build time.

.PARAMETER PackageVersion
    The OpenHarmony.NET package version shared by all six feed packages.

.PARAMETER OutputDirectory
    Directory that receives the fabricated .zip.

.PARAMETER ReleaseId
    Bundle release identifier (one safe path segment).

.EXAMPLE
    ./Tools/make-harmonyos-bundle.ps1 -PackageVersion "10.0.10-ohos.2-preview.1"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageVersion,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\Build\HarmonyOsBundle"),

    [string]$ReleaseId = "ohos-local-" + (Get-Date -Format "yyyyMMdd"),

    # Optional module.json5 ability orientation (e.g. "landscape"). Empty = template default.
    [string]$Orientation = ""
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 (.NET Framework) needs the zip assemblies loaded explicitly.
if ($null -eq ("System.IO.Compression.ZipArchiveMode" -as [type])) {
    Add-Type -AssemblyName System.IO.Compression
}
if ($null -eq ("System.IO.Compression.ZipFile" -as [type])) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}

$requiredPackageIds = @(
    "OpenHarmony.NET.Runtime.NativeAot.arm64-v8a",
    "OpenHarmony.NET.Runtime.NativeAot.x86_64",
    "OpenHarmony.NET.PublishAotCross",
    "OpenHarmony.NDK.Bindings",
    "OpenHarmony.NET.Player.Sdk",
    "OpenHarmony.NET.HapTooling"
)

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

$s_utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-NoBomUtf8([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, $script:s_utf8NoBom)
}

$nugetRoot = Join-Path $env:USERPROFILE ".nuget\packages"
$hapToolingDir = Join-Path $nugetRoot ("openharmony.net.haptooling\" + $PackageVersion)
if (-not (Test-Path $hapToolingDir)) {
    throw "OpenHarmony.NET.HapTooling $PackageVersion not found under $nugetRoot"
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("xengine-bundle-" + [Guid]::NewGuid().ToString("N"))
 New-Item -ItemType Directory -Path $staging | Out-Null
try {
    # Stage template + SDK catalog come from the HapTooling package layout.
    Copy-Item -LiteralPath (Join-Path $hapToolingDir "templates") -Destination (Join-Path $staging "templates") -Recurse
    Copy-Item -LiteralPath (Join-Path $hapToolingDir "schemas") -Destination (Join-Path $staging "schemas") -Recurse

    # Optional ability orientation pin. NOTE: on portrait-native panels, forcing landscape
    # gives the XComponent surface currentTransform=Rotate90 while the engine still presents
    # with preTransform=Identity — the compositor then horizontally mirrors the frame (observed
    # on a Mali-G610 tablet). Real landscape needs engine-side swapchain pre-rotation support;
    # leave this empty until that lands.
    if (-not [string]::IsNullOrWhiteSpace($Orientation)) {
        $moduleJson5Path = Join-Path $staging "templates\stage-player\entry\src\main\module.json5"
        $moduleJson5 = [IO.File]::ReadAllText($moduleJson5Path)
        $moduleJson5 = $moduleJson5.Replace('"exported": true,', "`"exported`": true,`n        `"orientation`": `"$Orientation`",")
        Write-NoBomUtf8 $moduleJson5Path $moduleJson5
    }

    # XEngine screen-orientation bridge overrides. The HapTooling stock page neither polls
    # engine orientation requests nor reports display rotation; the repo-owned versions under
    # Tools/harmonyos-stage-templates add that bridge (see xengine_player_abi.h — packed
    # request polling + setDisplayOrientation reporting). Overwrite the staged stock files so
    # every fabricated Bundle carries the bridge without hand-editing the extracted tree.
    $bridgeSourceRoot = Join-Path $PSScriptRoot "harmonyos-stage-templates"
    $bridgeOverrides = @(
        @{ Source = (Join-Path $bridgeSourceRoot "Index.ets")
           Target = "templates\stage-player\entry\src\main\ets\pages\Index.ets" },
        @{ Source = (Join-Path $bridgeSourceRoot "index.d.ts")
           Target = "templates\stage-player\entry\src\main\cpp\types\libxengine_host\index.d.ts" }
    )
    foreach ($override in $bridgeOverrides) {
        if (-not (Test-Path -LiteralPath $override.Source)) {
            throw ("Stage template override is missing: " + $override.Source)
        }
        Copy-Item -LiteralPath $override.Source -Destination (Join-Path $staging $override.Target) -Force
    }

    # Feed packages come from the local NuGet cache (cache folders are lower-cased ids).
    New-Item -ItemType Directory -Path (Join-Path $staging "feed") | Out-Null
    foreach ($id in $requiredPackageIds) {
        $cacheDir = Join-Path $nugetRoot ($id.ToLowerInvariant() + "\" + $PackageVersion)
        $nupkg = Join-Path $cacheDir ($id.ToLowerInvariant() + "." + $PackageVersion + ".nupkg")
        if (-not (Test-Path $nupkg)) {
            throw "Feed package not found: $nupkg"
        }
        Copy-Item -LiteralPath $nupkg -Destination (Join-Path $staging ("feed\" + $id + "." + $PackageVersion + ".nupkg"))
    }

    # Licenses + SBOM: presence is contract-checked; content documents provenance.
    New-Item -ItemType Directory -Path (Join-Path $staging "licenses") | Out-Null
    $licenseText = @"
XEngine OpenHarmony SDK Bundle - licenses
=========================================

This Bundle aggregates OpenHarmony.NET packages (MIT, https://github.com/OpenHarmony-NET)
and material assembled from the OpenHarmony.NET.HapTooling package. Each feed package
carries its own license in its nuspec/README. The Stage template and SDK catalog are
redistributed under the MIT license of OpenHarmony.NET.HapTooling.
"@
    Write-NoBomUtf8 (Join-Path $staging "licenses\LICENSE.txt") $licenseText

    $sbom = [ordered]@{
        spdxVersion = "SPDX-2.3"
        dataLicense = "CC0-1.0"
        SPDXID = "SPDXRef-DOCUMENT"
        name = "XEngine-OpenHarmony-SDK-Bundle-$ReleaseId"
        documentNamespace = "https://xengine.dev/spdx/openharmony-sdk-bundle/$ReleaseId"
        creationInfo = [ordered]@{
            created = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
            creators = @("Tool: XEngine make-harmonyos-bundle.ps1")
        }
        packages = @($requiredPackageIds | ForEach-Object {
            [ordered]@{
                name = $_
                SPDXID = ("SPDXRef-Package-" + ($_ -replace '[^A-Za-z0-9]', '-'))
                versionInfo = $PackageVersion
                downloadLocation = "NOASSERTION"
                filesAnalyzed = $false
                licenseConcluded = "MIT"
                licenseDeclared = "MIT"
                copyrightText = "NOASSERTION"
            }
        })
    }
    Write-NoBomUtf8 (Join-Path $staging "sbom.spdx.json") ($sbom | ConvertTo-Json -Depth 6)

    # SHA256SUMS over every staged file (standard "<sha256>  <relative path>" form).
    $allFiles = Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object FullName
    $sums = foreach ($file in $allFiles) {
        $relative = $file.FullName.Substring($staging.Length + 1).Replace("\", "/")
        (Get-Sha256 $file.FullName) + "  " + $relative
    }
    Write-NoBomUtf8 (Join-Path $staging "SHA256SUMS") ($sums -join "`n")

    # release-manifest.json over every file except the manifest itself.
    $files = foreach ($file in (Get-ChildItem -LiteralPath $staging -Recurse -File)) {
        $relative = $file.FullName.Substring($staging.Length + 1).Replace("\", "/")
        if ($relative -eq "release-manifest.json") { continue }
        [ordered]@{
            path = $relative
            length = $file.Length
            sha256 = (Get-Sha256 $file.FullName)
        }
    }
    $packages = @($requiredPackageIds | ForEach-Object {
        $feedPath = "feed/$_.$PackageVersion.nupkg"
        [ordered]@{
            id = $_
            version = $PackageVersion
            path = $feedPath
            sha256 = (Get-Sha256 (Join-Path $staging ($feedPath.Replace("/", "\"))))
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        releaseId = $ReleaseId
        packageVersion = $PackageVersion
        capabilities = @("playerPublish", "stageProject", "hapBuild", "hapVerify", "hdcDeploy")
        files = @($files)
        packages = $packages
    }
    Write-NoBomUtf8 (Join-Path $staging "release-manifest.json") ($manifest | ConvertTo-Json -Depth 6)

    # Zip with forward-slash entry names.
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $archivePath = Join-Path $OutputDirectory ("OpenHarmonySdkBundle-" + $ReleaseId + ".zip")
    if (Test-Path $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $staging -Recurse -File)) {
            $entryName = $file.FullName.Substring($staging.Length + 1).Replace("\", "/")
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Output ("Bundle: " + $archivePath)
    Write-Output ("Archive SHA256: " + (Get-Sha256 $archivePath))
    Write-Output ("Files: " + ($allFiles.Count + 1))
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
