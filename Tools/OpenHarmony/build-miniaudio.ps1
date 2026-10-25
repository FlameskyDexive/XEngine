# Cross-compiles libminiaudioex.so for openharmony-arm64 with the OHOS SDK clang
# and an OHAudio output backend (see miniaudioex_ohos.c). Output lands in
# Libraries/openharmony-arm64/native/ where the HarmonyOS build pipeline stages
# HAP libs — the same convention as the prebuilt FFmpeg libraries.
#
# Usage: pwsh Tools/OpenHarmony/build-miniaudio.ps1
[CmdletBinding()]
param(
    [string]$SdkNativeRoot = "$env:LOCALAPPDATA\OpenHarmony\Sdk\13\native",
    [string]$SourceRoot = "$env:TEMP\miniaudioex-src",
    [string]$BuildRoot = "$env:TEMP\miniaudioex-build"
)
$ErrorActionPreference = "Stop"

$upstream = "https://github.com/japajoe/miniaudioex.git"
$commit = "5900f73f647653defb2562ec2efc94fac3897ea3"
$hooksPatch = Join-Path $PSScriptRoot "miniaudioex-ohos-hook.patch"
$ohosSource = Join-Path $PSScriptRoot "miniaudioex_ohos.c"
$clangBin = Join-Path $SdkNativeRoot "llvm\bin"
$clang = Join-Path $clangBin "clang.exe"
$nm = Join-Path $clangBin "llvm-nm.exe"
$sysroot = Join-Path $SdkNativeRoot "sysroot"
$target = "aarch64-linux-ohos"

foreach ($tool in @($clang, $nm)) {
    if (-not (Test-Path $tool)) { throw "missing toolchain file: $tool" }
}

if (-not (Test-Path (Join-Path $SourceRoot ".git"))) {
    cmd /c "git clone --filter=blob:none $upstream $SourceRoot 2>&1" | Out-Null
}
cmd /c "git -C $SourceRoot fetch origin $commit 2>&1" | Out-Null
cmd /c "git -C $SourceRoot checkout --detach $commit 2>&1" | Out-Null
if ((git -C $SourceRoot rev-parse HEAD).Trim() -ne $commit) {
    throw "miniaudioex source did not resolve to $commit"
}
# Reset any previous hook application before replaying the patch.
git -C $SourceRoot checkout -- . 2>&1 | Out-Null
Remove-Item (Join-Path $SourceRoot "src\miniaudioex_ohos.c") -Force -ErrorAction SilentlyContinue

# Replay the OHOS hooks (guarded by __OHOS__; other platforms are untouched).
git -C $SourceRoot apply --unidiff-zero --check $hooksPatch
if ($LASTEXITCODE -ne 0) { throw "ohos hook patch does not apply to $commit" }
git -C $SourceRoot apply --unidiff-zero $hooksPatch
Copy-Item $ohosSource -Destination (Join-Path $SourceRoot "src\miniaudioex_ohos.c") -Force

$outputDirectory = Join-Path $BuildRoot "openharmony-arm64"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory "libminiaudioex.so"

$arguments = @(
    "--target=$target", "--sysroot=$sysroot", "-shared", "-O2", "-DNDEBUG", "-g0", "-fPIC",
    "-Iinclude", "-Iinclude\extras",
    # OHOS has none of the desktop/mobile device backends: null backend only,
    # the OHAudio renderer (miniaudioex_ohos.c) produces the real output.
    "-DMA_NO_ALSA", "-DMA_NO_PULSEAUDIO", "-DMA_NO_JACK", "-DMA_NO_OSS",
    "-DMA_NO_SNDIO", "-DMA_NO_AUDIO4", "-DMA_NO_AUDIOUNIT", "-DMA_NO_WASAPI",
    "-DMA_NO_DSOUND", "-DMA_NO_WINMM", "-DMA_NO_COREAUDIO", "-DMA_NO_OPENSL",
    "-DMA_NO_AAUDIO", "-DMA_NO_WEBAUDIO", "-DMA_NO_OBOE",
    "src\miniaudio.c", "src\miniaudioex.c", "src\miniaudioex_ohos.c", "src\extras\miniaudio_libvorbis.c",
    "-Wl,-soname,libminiaudioex.so",
    "-Wl,--build-id=sha1",
    "-lm",
    "-o", $output
)

Push-Location $SourceRoot
try {
    & $clang $arguments
    if ($LASTEXITCODE -ne 0) { throw "ohos clang failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

# hvigor strips unversioned .so files during packaging; pre-strip so the staged
# bytes equal the packaged bytes (the payload hash gate compares them verbatim).
$strip = Join-Path $SdkNativeRoot "llvm\bin\llvm-strip.exe"
if (-not (Test-Path $strip)) { throw "missing toolchain file: $strip" }
& $strip --strip-all $output
if ($LASTEXITCODE -ne 0) { throw "llvm-strip failed" }

# Every ma_ex_* entry point the runtime P/Invokes must be exported.
$runtimeImports = Select-String `
    -Path (Join-Path $PSScriptRoot "..\..\XEngine.Runtime\Audio\Native\MiniAudioExNative.cs") `
    -Pattern 'EntryPoint = "([^"]+)"' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Sort-Object -Unique
$exports = & $nm -D --defined-only $output |
    ForEach-Object { ($_ -split "\s+")[-1] } |
    Sort-Object -Unique
$missing = $runtimeImports | Where-Object { $_ -notin $exports }
if ($missing) { throw "missing Runtime exports: $($missing -join ', ')" }
foreach ($required in @("ma_ex_ohos_set_playback_active", "ma_ex_ohos_renderer_start")) {
    if ($required -notin $exports) { throw "missing OHOS export: $required" }
}

$destination = Join-Path $PSScriptRoot "..\..\Libraries\openharmony-arm64\native\libminiaudioex.so"
Copy-Item -LiteralPath $output -Destination $destination -Force
Write-Output "openharmony-arm64: $((Get-FileHash $output -Algorithm SHA256).Hash)"
Write-Output "installed: $destination"
