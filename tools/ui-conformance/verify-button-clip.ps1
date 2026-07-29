param(
    [double[]]$Scales = @(1.0, 1.5)
)

# Candidate-side product invariant: a text button narrower than its
# label CLIPS the label to its visual bounds. Picto's native <button>
# does not clip, so this is deliberately NOT a parity fixture — the
# btn-narrow state is hidden from the conformance catalog and asserted
# here instead.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}
$work = Join-Path $toolRoot "artifacts\button-clip"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$invariant = [Globalization.CultureInfo]::InvariantCulture

foreach ($scale in $Scales) {
    $png = Join-Path $work "btn-narrow@$($scale.ToString($invariant)).png"
    & $exe btn-narrow $png $scale.ToString($invariant) dark | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "btn-narrow capture failed at $scale." }
    $result = python -c @"
import numpy as np
from PIL import Image
img = np.asarray(Image.open(r'$png').convert('L')).astype(int)
bg = img[0, 0]
ink = np.abs(img - bg) > 10
ys, xs = np.where(ink)
scale = $($scale.ToString($invariant))
left, top = round(24 * scale), round(24 * scale)
right, bottom = round((24 + 60) * scale), round((24 + 32) * scale)
inside = xs.min() >= left and xs.max() <= right - 1 and ys.min() >= top and ys.max() <= bottom - 1
# The label must actually be wider than the box for the clip to matter:
# ink should reach the last interior column before the border.
reaches = xs.max() >= right - round(3 * scale)
print('PASS' if inside and reaches else f'FAIL inside={inside} reaches={reaches} x={xs.min()}..{xs.max()} y={ys.min()}..{ys.max()}')
"@
    if ($result -ne "PASS") {
        throw "Button clip invariant failed at ${scale}x: $result"
    }
    Write-Host "btn-narrow clip invariant PASS at ${scale}x ($result)"
}
