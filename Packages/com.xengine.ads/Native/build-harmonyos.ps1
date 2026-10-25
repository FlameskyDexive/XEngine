# Cross-compiles libxengine_ads.so for HarmonyOS (ohos ndk clang) for the
# emulator (x86_64-linux-ohos) and device (aarch64-linux-ohos), copies the
# outputs into the package Plugins tree, and refreshes the native dependency
# manifest (sha256 + build id per ABI). Mirrors com.xengine.xts.
[CmdletBinding()]
param(
    [string]$NdkRoot = "F:\Program Files\Huawei\DevEco Studio\sdk\default\openharmony\native"
)
$ErrorActionPreference = "Stop"
$native = $PSScriptRoot
$package = Split-Path $native

if (-not (Test-Path (Join-Path $NdkRoot "llvm\bin\clang.exe"))) {
    throw "ohos ndk not found at $NdkRoot; pass -NdkRoot (…\sdk\default\openharmony\native) or install the platform toolchain: xengine-cli install-platform-tools harmonyos"
}

$targets = @(
    @{ Triple = "x86_64-linux-ohos";  Out = "artifacts/ohos-x86_64"; Plugin = "Plugins/HarmonyOS/x86_64"; Abi = "x86_64";  ManifestPlatform = "harmonyos-x86_64" },
    @{ Triple = "aarch64-linux-ohos"; Out = "artifacts/ohos-arm64";  Plugin = "Plugins/HarmonyOS/arm64";  Abi = "arm64-v8a"; ManifestPlatform = "harmonyos-arm64" }
)

$sources = @(
    "$native\source\xengine_ads_napi.cpp"
) | ForEach-Object { ($_ -replace '\\', '/') }

$clang = Join-Path $NdkRoot "llvm\bin\clang.exe"
$readelf = Join-Path $NdkRoot "llvm\bin\llvm-readelf.exe"

foreach ($t in $targets) {
    $outDir = Join-Path $native $t.Out
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $so = "$outDir/libxengine_ads.so"

    $args = @(
        "--target=$($t.Triple)",
        "--sysroot=$(Join-Path $NdkRoot 'sysroot')",
        "-shared", "-fPIC", "-O2", "-fvisibility=hidden",
        "-std=c++17", "-fno-exceptions", "-fno-rtti",
        "-I$(Join-Path $NdkRoot 'sysroot\usr\include')"
    ) + $sources + @("-o", $so, "-lace_napi.z", "-Wl,--build-id=sha1")

    $log = & $clang @args 2>&1 | Out-String
    Set-Content -Encoding utf8 (Join-Path $outDir "build.log") $log
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $so)) {
        $log | Write-Host
        throw "ohos cross-compile failed for $($t.Triple)"
    }
    Write-Output "$($t.Triple): compiled ok"

    # GNU build id (matches HarmonyOS receipt conventions). readelf emits harmless
    # "note overflows container" warnings on ohos build-id notes; route stderr through
    # cmd so $ErrorActionPreference=Stop does not treat them as terminating.
    $notes = cmd /c "`"$readelf`" -n `"$so`" 2>&1" | Out-String
    $buildId = ""
    if ($notes -match "Build ID:\s*([0-9a-f]{16})[0-9a-f]*") { $buildId = $Matches[1] }
    $sha = (Get-FileHash $so -Algorithm SHA256).Hash.ToLowerInvariant()

    $pluginDir = Join-Path $package $t.Plugin
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    Copy-Item $so (Join-Path $pluginDir "libxengine_ads.so") -Force

    # refresh the manifest entry (create if absent)
    $manifestPath = Join-Path $native "native-manifest.json"
    $manifest = Get-Content $manifestPath -Raw
    $entryPattern = "(?s)    `"" + $t.ManifestPlatform + "`": \{.*?\r?\n    \},?\r?\n"
    $newEntry = "    `"" + $t.ManifestPlatform + "`": {" + [Environment]::NewLine +
        "      `"library`": `"" + ($t.Plugin -replace '\\','/') + "/libxengine_ads.so`"," + [Environment]::NewLine +
        "      `"abi`": `"" + $t.Abi + "`"," + [Environment]::NewLine +
        "      `"sha256`": `"$sha`"," + [Environment]::NewLine +
        "      `"source`": `"Native/build-harmonyos.ps1 (ohos ndk clang, $((Get-Item $NdkRoot).FullName))`"," + [Environment]::NewLine +
        "      `"buildId`": `"$buildId`"" + [Environment]::NewLine +
        "    }," + [Environment]::NewLine
    if ($manifest -match $entryPattern) {
        $manifest = $manifest -replace $entryPattern, $newEntry
    } else {
        # insert before the closing brace of "platforms"
        $manifest = $manifest -replace '(\r?\n  \})', ([Environment]::NewLine + $newEntry + "  }")
    }
    Set-Content -Encoding utf8 $manifestPath $manifest
    Write-Output "$($t.Triple): sha256=$sha buildId=$buildId"
}
Write-Output "manifest updated"
