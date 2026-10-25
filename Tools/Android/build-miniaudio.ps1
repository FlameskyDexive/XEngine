[CmdletBinding()]
param(
    [string]$NdkRoot = "F:\AndroidPlayer\NDK",
    [string]$SourceRoot = "F:\XEngine\miniaudioex-source",
    [string]$BuildRoot = "F:\XEngine\miniaudioex-build",
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"
$upstream = "https://github.com/japajoe/miniaudioex.git"
$commit = "5900f73f647653defb2562ec2efc94fac3897ea3"
$sourcePatch = Join-Path $PSScriptRoot "miniaudioex-default-playback.patch"
$toolBin = Join-Path $NdkRoot "toolchains\llvm\prebuilt\windows-x86_64\bin"
$readelf = Join-Path $toolBin "llvm-readelf.exe"
$nm = Join-Path $toolBin "llvm-nm.exe"

if (-not (Test-Path (Join-Path $SourceRoot ".git"))) {
    git clone --filter=blob:none $upstream $SourceRoot
}
git -C $SourceRoot fetch origin $commit
git -C $SourceRoot checkout --detach $commit
if ((git -C $SourceRoot rev-parse HEAD).Trim() -ne $commit) {
    throw "miniaudioex source did not resolve to $commit"
}
git -C $SourceRoot restore --source=$commit --staged --worktree -- src/miniaudioex.c
git -C $SourceRoot apply --unidiff-zero --check $sourcePatch
if ($LASTEXITCODE -ne 0) { throw "miniaudioex source patch does not apply to $commit" }
git -C $SourceRoot apply --unidiff-zero $sourcePatch
if ($LASTEXITCODE -ne 0) { throw "Failed to apply miniaudioex source patch" }

$targets = @(
    @{
        Rid = "android-arm64"
        Compiler = "aarch64-linux-android24-clang.cmd"
        Extra = @()
    },
    @{
        Rid = "android-arm"
        Compiler = "armv7a-linux-androideabi24-clang.cmd"
        Extra = @("-latomic")
    }
)

foreach ($target in $targets) {
    $outputDirectory = Join-Path $BuildRoot $target.Rid
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $output = Join-Path $outputDirectory "libminiaudioex.so"
    $arguments = @(
        "-shared", "-O2", "-DNDEBUG", "-g0", "-fPIC",
        "-Iinclude", "-Iinclude\extras",
        "src\miniaudio.c", "src\miniaudioex.c", "src\extras\miniaudio_libvorbis.c",
        "-Wl,-z,max-page-size=16384",
        "-Wl,-soname,libminiaudioex.so",
        "-Wl,--build-id=sha1",
        "-lm", "-ldl", "-llog", "-landroid", "-lOpenSLES"
    ) + $target.Extra + @("-o", $output)
    Push-Location $SourceRoot
    try {
        & (Join-Path $toolBin $target.Compiler) $arguments
        if ($LASTEXITCODE -ne 0) { throw "$($target.Compiler) failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }

    $loadLines = & $readelf -lW $output | Select-String "LOAD"
    if ($loadLines.Count -eq 0 -or ($loadLines | Where-Object { $_.Line -notmatch "0x4000\s*$" })) {
        throw "$($target.Rid) does not have 16 KB PT_LOAD alignment"
    }

    $runtimeImports = Select-String `
        -Path (Join-Path $RepositoryRoot "XEngine.Runtime\Audio\Native\MiniAudioExNative.cs") `
        -Pattern 'EntryPoint = "([^"]+)"' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } |
        Sort-Object -Unique
    $exports = & $nm -D --defined-only $output |
        ForEach-Object { ($_ -split "\s+")[-1] } |
        Sort-Object -Unique
    $missing = $runtimeImports | Where-Object { $_ -notin $exports }
    if ($missing) {
        throw "$($target.Rid) is missing Runtime exports: $($missing -join ', ')"
    }

    $destination = Join-Path $RepositoryRoot "Libraries\$($target.Rid)\native\libminiaudioex.so"
    Copy-Item -LiteralPath $output -Destination $destination -Force
    Write-Host "$($target.Rid): $((Get-FileHash $output -Algorithm SHA256).Hash)"
}
