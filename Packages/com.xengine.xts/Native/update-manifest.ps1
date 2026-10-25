# Rebuilds the native library, copies it into the package Plugins directory,
# and refreshes Native/native-manifest.json (sha256 + build id) so the
# manifest always pins exactly the shipped binary.
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$native = $PSScriptRoot
$package = Split-Path $native

$out = pwsh -NoLogo -NoProfile -File (Join-Path $native 'build-windows.ps1') 2>&1
$out | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'native build failed' }

$source = Join-Path $native 'artifacts/windows-x64/xengine_xts.dll'
$target = Join-Path $package 'Plugins/Windows/x64/xengine_xts.dll'
Copy-Item $source $target -Force
$sha = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
$buildId = $sha.Substring(0, 16)

$manifestPath = Join-Path $native 'native-manifest.json'
$manifest = Get-Content $manifestPath -Raw
$manifest = $manifest -replace '"sha256": "[0-9a-f]{64}"', "`"sha256`": `"$sha`""
$manifest = $manifest -replace '"buildId": "[0-9a-f]{16}"', "`"buildId`": `"$buildId`""
Set-Content -Encoding utf8 $manifestPath $manifest
Write-Output "manifest updated: sha256=$sha buildId=$buildId"
