param(
    [double[]]$Scales = @(1.0, 1.5)
)

# Candidate-side button invariants (no Picto parity references exist):
#
# 1. CLIP: a text button narrower than its label clips the label to its
#    visual bounds. Glyph pixels are isolated UNAMBIGUOUSLY by
#    subtracting the chrome-only blank-label twin (btn-narrow-blank)
#    from the labelled capture — the border can no longer satisfy the
#    assertions. A deliberate unclipped negative control
#    (btn-narrow-unclipped) must FAIL the same test, proving the mask
#    detects escapes.
# 2. HOVER RECONCILE: hover settled -> disabled -> pointer leaves ->
#    re-enabled must end byte-identical to idle; stale hover fill must
#    never replay.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}
$work = Join-Path $toolRoot "artifacts\button-invariants"
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $work | Out-Null
$invariant = [Globalization.CultureInfo]::InvariantCulture

function Invoke-Capture([string]$state, [double]$scale) {
    $png = Join-Path $work "$state@$($scale.ToString($invariant)).png"
    & $exe $state $png $scale.ToString($invariant) dark | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$state capture failed at $scale." }
    return $png
}

foreach ($scale in $Scales) {
    $labelled = Invoke-Capture "btn-narrow" $scale
    $blank = Invoke-Capture "btn-narrow-blank" $scale
    $unclipped = Invoke-Capture "btn-narrow-unclipped" $scale

    $result = python -c @"
import numpy as np
from PIL import Image
scale = $($scale.ToString($invariant))
blank = np.asarray(Image.open(r'$blank').convert('L')).astype(int)
def glyphs(path):
    img = np.asarray(Image.open(path).convert('L')).astype(int)
    return np.abs(img - blank) > 10
left, top = round(24*scale), round(24*scale)
right, bottom = round((24+60)*scale), round((24+32)*scale)
def check(mask):
    ys, xs = np.where(mask)
    if len(xs) == 0:
        return False, False
    inside = xs.min() >= left and xs.max() <= right-1 and ys.min() >= top and ys.max() <= bottom-1
    reaches = xs.max() >= right - round(3*scale)
    return inside, reaches
ci, cr = check(glyphs(r'$labelled'))
ui, ur = check(glyphs(r'$unclipped'))
clip_ok = ci and cr
control_fails = not ui  # glyphs must ESCAPE in the unclipped control
print('PASS' if clip_ok and control_fails else
      f'FAIL clip(inside={ci},reaches={cr}) control(escapes={not ui})')
"@
    if ($result -ne "PASS") {
        throw "Button clip invariant failed at ${scale}x: $result"
    }
    Write-Host "clip invariant + negative control PASS at ${scale}x"

    $idle = Invoke-Capture "btn-secondary" $scale
    $reconcile = Invoke-Capture "btn-hover-reconcile" $scale
    $idleHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $idle).Hash
    $reconcileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $reconcile).Hash
    if ($idleHash -ne $reconcileHash) {
        throw "Hover reconcile invariant failed at ${scale}x: final enabled frame differs from idle."
    }
    Write-Host "hover reconcile PASS at ${scale}x (byte-identical to idle)"
}
