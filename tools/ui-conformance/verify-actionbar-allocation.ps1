param(
    [double[]]$Scales = @(1.0, 1.5)
)

# Candidate-side ActionBar invariant: Content + Fixed + Fill buttons,
# left- and right-aligned, must stay entirely inside the supplied
# 260px bar allocation on the 340px canvas. If Fill consulted ambient
# window width instead of the remaining bar allocation, ink would cross
# the allocation's right edge and this fails.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}
$work = Join-Path $toolRoot "artifacts\button-invariants"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$invariant = [Globalization.CultureInfo]::InvariantCulture

foreach ($scale in $Scales) {
    $png = Join-Path $work "bar-allocation@$($scale.ToString($invariant)).png"
    & $exe bar-allocation $png $scale.ToString($invariant) dark | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "bar-allocation capture failed at $scale." }
    $result = python -c @"
import numpy as np
from PIL import Image
scale = $($scale.ToString($invariant))
img = np.asarray(Image.open(r'$png').convert('L')).astype(int)
bg = img[0, 0]
ink = np.abs(img - bg) > 10
ys, xs = np.where(ink)
left, right = round(24*scale), round((24+260)*scale)
inside = xs.min() >= left and xs.max() <= right - 1
# Fill must actually consume the remaining allocation: ink reaches the
# right edge region on the left-aligned row, and the right-aligned row
# starts at the allocation's left edge region.
reaches_right = xs.max() >= right - round(3*scale)
reaches_left = xs.min() <= left + round(3*scale)
print('PASS' if inside and reaches_right and reaches_left else
      f'FAIL inside={inside} right={reaches_right} left={reaches_left} x={xs.min()}..{xs.max()}')
"@
    if ($result -ne "PASS") {
        throw "ActionBar allocation invariant failed at ${scale}x: $result"
    }
    Write-Host "ActionBar allocation invariant PASS at ${scale}x"
}
