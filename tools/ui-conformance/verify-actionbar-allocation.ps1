param(
    [double[]]$Scales = @(1.0, 1.5)
)

# Candidate-side ActionBar invariant, asserted PER ROW: the left-aligned
# row (Content + Fixed(70) + Fill) and the right-aligned row (same three)
# each resolve inside the supplied 260px bar allocation on the 340px
# canvas. Each row must contain exactly three button rectangles; the
# Content width must equal the measured "OK" intrinsic (label + 16px
# padding per side + 1px border per side), the Fixed width must equal
# 70px, the gaps must equal the 8px ActionGap, and the Fill width must
# equal EXACTLY the remaining allocation — anchored at the allocation
# edge on each row's packed side. If Fill consulted ambient window width
# instead of the remaining bar allocation, the fill rectangle would be
# too wide and cross the allocation edge; if it under-resolved, the row
# would not reach the edge. No combined-image assertion exists.

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

# Candidate-measured "OK" run width. ActionBar promotes Natural-height
# items to WORKSPACE controls (12px label, 12px padding per side), so
# the Content assertion proves that border-box formula rather than
# accepting whatever rectangle rendered.
$measure = "OK" | & $exe --measure 12
if ($LASTEXITCODE -ne 0) { throw "--measure probe failed." }
$okLine = $measure | Where-Object { $_ -match "^OK\t" }
if (-not $okLine) { throw "--measure returned no OK row." }
$okWidth = [double]::Parse(
    ($okLine -split "\t")[1].Split(",")[-1], $invariant)

foreach ($scale in $Scales) {
    $png = Join-Path $work "bar-allocation@$($scale.ToString($invariant)).png"
    & $exe bar-allocation $png $scale.ToString($invariant) dark | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "bar-allocation capture failed at $scale." }
    $result = python -c @"
import numpy as np
from PIL import Image
scale = $($scale.ToString($invariant))
ok_css = $($okWidth.ToString($invariant)) + 26.0  # label + 2*12 pad + 2*1 border
img = np.asarray(Image.open(r'$png').convert('L')).astype(int)
bg = img[0, 0]
# Threshold 5 keeps the 4% white button fill (~+9 grey on dark) as ink,
# so each button projects as ONE contiguous column run; the 8px gaps
# are exact background and project as zero.
ink = np.abs(img - bg) > 5
left, right = round(24*scale), round((24+260)*scale)
gap_px = 8*scale
failures = []

def runs(mask_cols):
    xs = np.flatnonzero(mask_cols)
    if xs.size == 0:
        return []
    breaks = np.flatnonzero(np.diff(xs) > 1)
    starts = np.concatenate(([0], breaks + 1))
    ends = np.concatenate((breaks, [xs.size - 1]))
    return [(int(xs[a]), int(xs[b])) for a, b in zip(starts, ends)]

for label, y0, y1, packed in (
        ('left-row',  round(24*scale), round(64*scale),  'left'),
        ('right-row', round(80*scale), round(120*scale), 'right')):
    cols = ink[y0:y1].any(axis=0)
    row_runs = runs(cols)
    if len(row_runs) != 3:
        failures.append(f'{label}: {len(row_runs)} rectangles, want 3 ({row_runs})')
        continue
    (a0, a1), (b0, b1), (c0, c1) = row_runs
    widths = (a1 - a0 + 1, b1 - b0 + 1, c1 - c0 + 1)
    if packed == 'left':
        content_w, fixed_w, fill_w = widths
        content_span, fill_span = (a0, a1), (c0, c1)
    else:
        content_w, fixed_w, fill_w = widths
        content_span, fill_span = (a0, a1), (c0, c1)
    # Every rectangle inside the allocation.
    if a0 < left or c1 > right - 1:
        failures.append(f'{label}: ink {a0}..{c1} outside allocation {left}..{right-1}')
    # The packed side is anchored at its allocation edge; a full row of
    # three with correct widths then anchors the far side too.
    if packed == 'left' and abs(a0 - left) > 1:
        failures.append(f'{label}: first rect starts at {a0}, allocation edge {left}')
    if packed == 'right' and abs(c1 - (right - 1)) > 1:
        failures.append(f'{label}: last rect ends at {c1}, allocation edge {right-1}')
    # Fixed(70) is the middle rectangle on both rows.
    if abs(fixed_w - 70*scale) > 1.5:
        failures.append(f'{label}: Fixed width {fixed_w}, want {70*scale}')
    # Content = measured label + padding + border (CSS border-box).
    if abs(content_w - ok_css*scale) > 1.5:
        failures.append(f'{label}: Content width {content_w}, want {ok_css*scale:.2f}')
    # Both gaps are the 8px ActionGap.
    for gname, g in (('gap1', b0 - a1 - 1), ('gap2', c0 - b1 - 1)):
        if abs(g - gap_px) > 1.5:
            failures.append(f'{label}: {gname}={g}, want {gap_px}')
    # Fill consumes EXACTLY the remaining allocation.
    expected_fill = 260*scale - ok_css*scale - 70*scale - 2*gap_px
    if abs(fill_w - expected_fill) > 2:
        failures.append(f'{label}: Fill width {fill_w}, want {expected_fill:.2f}')

print('PASS' if not failures else 'FAIL ' + ' | '.join(failures))
"@
    if ($result -ne "PASS") {
        throw "ActionBar allocation invariant failed at ${scale}x: $result"
    }
    Write-Host "ActionBar allocation invariant PASS at ${scale}x (per-row, Content/Fixed/Fill proven)"
}
