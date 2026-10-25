# Builds Native xengine_xts.dll with the MSVC toolchain (cl.exe via vcvars64).
# No CMake is required on Windows; CMakeLists.txt remains the entry point for
# the HarmonyOS NDK build. Output:
#   <this dir>/artifacts/windows-x64/xengine_xts.dll
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$nativeDir = $PSScriptRoot
$artifacts = Join-Path $nativeDir "artifacts/windows-x64"
$objDir = Join-Path $artifacts "obj"
New-Item -ItemType Directory -Force -Path $objDir | Out-Null

$vcvars = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vcvars) { throw "vcvars64.bat not found; install VS 2022 Build Tools (C++ workload)" }

$sources = @(
    "$nativeDir\xengine_xts_abi\xengine_xts_abi.c",
    "$nativeDir\quickjs\quickjs.c",
    "$nativeDir\quickjs\cutils.c",
    "$nativeDir\quickjs\libregexp.c",
    "$nativeDir\quickjs\libunicode.c",
    "$nativeDir\quickjs\dtoa.c"
)

# A batch file sidesteps cmd.exe re-splitting the quoted argument string.
# /utf-8 keeps the UTF-8 upstream comments from being misread as GBK.
$clLine = "cl /nologo /utf-8 /std:c11 /W2 /O2 /D_CRT_SECURE_NO_WARNINGS /DXTS_BUILDING /DXTS_NO_ATOMICS" +
    ' /DCONFIG_VERSION=\"2025-04-26\"' +
    " /I`"$nativeDir\quickjs`" /I`"$nativeDir\quickjs\msvc-compat`" /I`"$nativeDir\xengine_xts_abi`"" +
    # forward slashes: a trailing backslash inside quotes is read as an escaped
    # quote by the CRT argv splitter and would swallow the remaining arguments
    " /LD /Fe:`"$($artifacts -replace '\\','/')/xengine_xts.dll`" /Fo`"$($objDir -replace '\\','/')/`"" +
    " $(($sources | ForEach-Object { ($_ -replace '\\','/') }) -join ' ')" +
    " /link /IMPLIB:`"$objDir\xengine_xts.lib`""
$cmdLines = @("@echo off", "call `"$vcvars`" >nul", $clLine)
$cmdFile = Join-Path $artifacts "build-cl.cmd"
$cmdLines | Set-Content -Encoding ascii $cmdFile

$output = cmd /c "`"$cmdFile`" 2>&1" | Out-String
$output | Set-Content -Encoding utf8 (Join-Path $artifacts "build.log")
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $artifacts "xengine_xts.dll"))) {
    $output | Write-Host
    throw "cl.exe build failed with exit code $LASTEXITCODE"
}
$dll = Join-Path $artifacts "xengine_xts.dll"
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    schema = "xengine.xts.native.v1"
    platform = "windows-x64"
    configuration = $Configuration
    defines = @("XTS_NO_ATOMICS", "XTS_BUILDING")
    dll = $dll
    sha256 = $hash
    builtUtc = [DateTime]::UtcNow.ToString("o")
} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $artifacts "xengine_xts.dll.json")
Write-Output "built=$dll"
Write-Output "sha256=$hash"
