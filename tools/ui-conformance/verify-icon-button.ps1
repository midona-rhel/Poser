param(
    [double[]]$Scales = @(1.0, 1.25, 1.5)
)

# Momentary Icon Button invariants:
# - real release-inside, drag-cancel, keyboard, and disabled sequences;
# - default and explicit sizing independent of ambient width;
# - hover exit and disable/re-enable reconciliation return to exact idle;
# - the flat background region at the fixed-timestep midpoint is byte-exact
#   with the Picto cubic-bezier(.4,0,.22,1) reference.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}

& $exe --icon-button-behavior
if ($LASTEXITCODE -ne 0) {
    throw "Icon Button input/size invariants failed."
}

$work = Join-Path $toolRoot "artifacts\icon-button-invariants"
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $work | Out-Null
$invariant = [Globalization.CultureInfo]::InvariantCulture

function Invoke-Capture([string]$state, [double]$scale) {
    $suffix = [string]::Format(
        $invariant, "{0:0.##}", $scale).Replace(".", "p")
    $png = Join-Path $work "$state@$suffix.png"
    & $exe $state $png $scale.ToString($invariant) dark | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$state capture failed at ${scale}x."
    }
    return $png
}

foreach ($scale in $Scales) {
    $idle = Invoke-Capture "icon-button-idle" $scale
    $exit = Invoke-Capture "icon-button-hover-exit" $scale
    $reconcile = Invoke-Capture "icon-button-hover-reconcile" $scale
    $idleHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $idle).Hash
    $exitHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exit).Hash
    $reconcileHash = (
        Get-FileHash -Algorithm SHA256 -LiteralPath $reconcile).Hash
    if ($idleHash -ne $exitHash) {
        throw "Hover exit differs from idle at ${scale}x."
    }
    if ($idleHash -ne $reconcileHash) {
        throw "Disable/re-enable replayed stale hover at ${scale}x."
    }
    Write-Host `
        "idle reconciliation PASS at ${scale}x (exit and re-enable byte-identical)"
}

$reference = Join-Path $toolRoot `
    "artifacts\picto\icon-button-hover-mid@dark@1.png"
$candidate = Join-Path $toolRoot `
    "artifacts\crystarium\icon-button-hover-mid@dark@1.png"
if (!(Test-Path -LiteralPath $reference) -or
    !(Test-Path -LiteralPath $candidate)) {
    throw "Run '.\run.ps1 icon-button -Scales 1 -Themes dark' before midpoint verification."
}
$midpoint = python -c @"
import numpy as np
from PIL import Image
r = np.asarray(Image.open(r'$reference').convert('RGBA'))
c = np.asarray(Image.open(r'$candidate').convert('RGBA'))
# Interior strips of the 28px button, outside the centered 16px glyph
# and away from the 5px rounded corners: background/opacity transition only.
mask = np.zeros(r.shape[:2], dtype=bool)
mask[30:46, 27:30] = True
mask[30:46, 46:49] = True
same = np.array_equal(r[mask], c[mask])
changed = not np.all(r[mask] == r[0, 0])
print('PASS' if same and changed else
      f'FAIL same={same} changed_from_idle={changed} max={np.abs(r[mask].astype(int)-c[mask].astype(int)).max()}')
"@
if ($midpoint -ne "PASS") {
    throw "150ms easing midpoint invariant failed: $midpoint"
}
Write-Host "150ms easing midpoint PASS (flat transition region byte-exact to Picto)"

