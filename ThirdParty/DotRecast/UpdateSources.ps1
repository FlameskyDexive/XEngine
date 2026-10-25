# Sync the six core DotRecast libraries from an upstream checkout into this folder.
# Usage: powershell -File ThirdParty/DotRecast/UpdateSources.ps1 D:\Engine\DotRecast
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$DotRecastSrc
)

if (-not (Test-Path -Path $DotRecastSrc -PathType Container)) {
    Write-Error "Directory '$DotRecastSrc' not found."
    exit 1
}

$Modules = @(
    'DotRecast.Core',
    'DotRecast.Recast',
    'DotRecast.Detour',
    'DotRecast.Detour.TileCache',
    'DotRecast.Detour.Crowd',
    'DotRecast.Detour.Dynamic'
)

$DestRoot = $PSScriptRoot
Write-Host "Syncing DotRecast from '$DotRecastSrc' to '$DestRoot'"

foreach ($Module in $Modules) {
    $srcUnderSrc = Join-Path $DotRecastSrc ("src\" + $Module)
    $srcAtRoot = Join-Path $DotRecastSrc $Module
    $srcPath = if (Test-Path $srcUnderSrc) { $srcUnderSrc } elseif (Test-Path $srcAtRoot) { $srcAtRoot } else { $null }
    if ($null -eq $srcPath) {
        Write-Warning "Module '$Module' not found. Skipping."
        continue
    }

    $destPath = Join-Path $DestRoot $Module
    Write-Host "  $Module"
    & robocopy $srcPath $destPath /E /XD bin obj /XF *.csproj *.user *.suo *.meta *.asmdef /NFL /NDL /NJH /NJS /nc /ns /np
    if ($LASTEXITCODE -ge 8) {
        Write-Error "robocopy failed for $Module (code $LASTEXITCODE)"
        exit 1
    }
}

$license = Join-Path $DotRecastSrc 'LICENSE.txt'
if (Test-Path $license) {
    Copy-Item $license (Join-Path $DestRoot 'LICENSE.txt') -Force
}

Write-Host 'Sync completed. Re-apply the vendored *.csproj files if upstream layout changed.'
