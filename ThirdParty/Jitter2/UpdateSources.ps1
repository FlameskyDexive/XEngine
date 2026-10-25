# Sync the Jitter2 kernel and its test suite from an upstream checkout into this folder.
#
# Usage: powershell -File ThirdParty/Jitter2/UpdateSources.ps1 D:\Engine\jitterphysics2
#
# IMPORTANT: XEngine maintains local modifications to the Jitter2 kernel (see PATCHES.md).
# A plain sync would overwrite them. This script therefore refreshes .upstream-baseline/
# only by default, so you can three-way diff "what upstream changed" against
# "what we changed" before touching the live source:
#
#   1. powershell -File UpdateSources.ps1 <checkout>              # refresh baseline only
#   2. git diff --stat -- .upstream-baseline                       # what upstream changed
#   3. git diff --no-index .upstream-baseline/Jitter2 Jitter2      # our patches, for reapplying
#   4. reapply / merge, then commit the baseline together with the live source
#
# Pass -Force to also overwrite the live Jitter2/ and Tests/ trees. Only do that when
# there are no local patches to preserve, or after you have captured them.
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$JitterSrc,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -Path $JitterSrc -PathType Container)) {
    Write-Error "Directory '$JitterSrc' not found."
    exit 1
}

# Accept either the repository root or the src/ directory.
$srcRoot = if (Test-Path (Join-Path $JitterSrc 'src\Jitter2')) { Join-Path $JitterSrc 'src' } else { $JitterSrc }
$kernelSrc = Join-Path $srcRoot 'Jitter2'
$testsSrc = Join-Path $srcRoot 'JitterTests'

if (-not (Test-Path $kernelSrc -PathType Container)) {
    Write-Error "Could not find a Jitter2 kernel under '$JitterSrc'."
    exit 1
}

$destRoot = $PSScriptRoot

# robocopy exit codes below 8 are success (0 = no change, 1 = copied, 2/3 = extras present).
function Invoke-Sync([string]$from, [string]$to, [string]$label) {
    Write-Host "  $label"
    & robocopy $from $to /MIR /XD bin obj _package /XF *.csproj *.user *.suo /NFL /NDL /NJH /NJS /nc /ns /np
    if ($LASTEXITCODE -ge 8) {
        Write-Error "robocopy failed for $label (code $LASTEXITCODE)"
        exit 1
    }
}

Write-Host "Refreshing pristine upstream baseline from '$srcRoot'"
Invoke-Sync $kernelSrc (Join-Path $destRoot '.upstream-baseline\Jitter2') 'baseline: Jitter2'
if (Test-Path $testsSrc -PathType Container) {
    Invoke-Sync $testsSrc (Join-Path $destRoot '.upstream-baseline\JitterTests') 'baseline: JitterTests'
}

# Record exactly which upstream revision the baseline represents.
Push-Location $JitterSrc
try {
    $commit = (& git rev-parse HEAD 2>$null)
    $describe = (& git describe --tags 2>$null)
    $when = (& git log -1 --format=%cI 2>$null)
}
finally {
    Pop-Location
}

$stamp = @(
    'Pristine upstream snapshot of Jitter Physics 2, used to three-way diff XEngine',
    'kernel patches (../PATCHES.md) against upstream when syncing a new version.',
    '',
    'Do not edit anything under this folder, and do not reference it from a project.',
    '',
    "Upstream:  https://github.com/notgiven688/jitterphysics2",
    "Commit:    $commit",
    "Describe:  $describe",
    "Committed: $when",
    "Synced:    $(Get-Date -Format 'yyyy-MM-dd')"
) -join [Environment]::NewLine
Set-Content -Path (Join-Path $destRoot '.upstream-baseline\UPSTREAM.txt') -Value $stamp -Encoding UTF8

$license = Join-Path $JitterSrc 'LICENSE'
if (Test-Path $license) {
    Copy-Item $license (Join-Path $destRoot 'LICENSE') -Force
}

if ($Force) {
    Write-Host 'Overwriting live source (-Force): local patches in these trees will be lost.'
    Invoke-Sync $kernelSrc (Join-Path $destRoot 'Jitter2') 'live: Jitter2'
    if (Test-Path $testsSrc -PathType Container) {
        Invoke-Sync $testsSrc (Join-Path $destRoot 'Tests') 'live: Tests'
    }
    Write-Host 'Sync completed. Reapply the patches listed in PATCHES.md, then rebuild and run both test suites.'
}
else {
    Write-Host ''
    Write-Host 'Baseline refreshed; live source left untouched. Next:'
    Write-Host '  git diff --stat -- .upstream-baseline                      # what upstream changed'
    Write-Host '  git diff --no-index .upstream-baseline/Jitter2 Jitter2     # our patches'
}
