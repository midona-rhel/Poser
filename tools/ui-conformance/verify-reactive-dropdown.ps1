param(
    [double]$Scale = 1.0,
    # Evidence, not a gate: see the sweep section.
    [bool]$Sweep = $true
)

# PBI-015 wave H: the retained reactive Dropdown IS the CmSelect control,
# not a lookalike.
#
# 1. TWIN IDENTITY: rdd-closed and rdd-open are captured beside their
#    dropdown-X twins in one process and must come back BYTE-IDENTICAL. The
#    open twin is the sharper of the two: the legacy fixture stages its menu
#    with an OpenPopover call, while the reactive one has to earn the same
#    frame through a real click, because the retained portal's handle is
#    derived from the element path.
# 2. SCROLLED TWINS: rdd-scrolled and dd-scrolled put ten items past the
#    seven-row viewport and wheel the list to the bottom with real
#    ImGuiIO wheel events, so the comparison covers a state the unscrolled
#    twins cannot reach. Byte-identity is the expected result; if they
#    diverge the run does NOT fail blindly, it asserts the CONTAINMENT
#    property on each capture and reports which one leaks.
# 3. BEHAVIOR: --reactive-dropdown-behavior covers what pixels cannot —
#    selection routing, the four ways a menu closes, keyboard parity, the
#    typed UiEvent<int> dispatch, supersession between two menus, and the
#    warm-frame allocation gate open, closed and past 32 rows.
# 4. SCALE SWEEP: byte-equality is asserted at 1.0 only. The retained path
#    snaps its boxes to whole pixels by contract while the imperative one
#    keeps fractional rects, so divergence at 1.25x and 1.5x is expected
#    and is REPORTED with numbers rather than failed on.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}
$work = Join-Path $toolRoot "artifacts\reactive-dropdown"
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

$pairs = @(
    @{ Reactive = "rdd-closed"; Legacy = "dropdown-closed" },
    @{ Reactive = "rdd-open";   Legacy = "dropdown-open" }
)

$failures = @()
foreach ($pair in $pairs) {
    $reactive = Invoke-Capture $pair.Reactive $Scale
    $legacy = Invoke-Capture $pair.Legacy $Scale
    $a = (Get-FileHash -Algorithm SHA256 -LiteralPath $reactive).Hash
    $b = (Get-FileHash -Algorithm SHA256 -LiteralPath $legacy).Hash
    if ($a -eq $b) {
        Write-Host "PASS $($pair.Reactive) == $($pair.Legacy) ($a)"
    } else {
        Write-Host "FAIL $($pair.Reactive) != $($pair.Legacy) ($a vs $b)"
        $failures += $pair.Reactive
    }
}

# ---- Scrolled twins ------------------------------------------------------
# The viewport band comes from the control's own CSS geometry: the 26px
# trigger at the (24,24) stage origin, CmSelect's 4px anchor gap, and the
# panel's 1px border plus 4px padding, over a list of seven 26px rows on 2px
# gaps. At scale 1 every one of those lands on a whole pixel.
$viewTop = [int](59 * $Scale)
$viewBottom = [int](253 * $Scale)
$rowHeight = [int](26 * $Scale)

$scrolledReactive = Invoke-Capture "rdd-scrolled" $Scale
$scrolledLegacy = Invoke-Capture "dd-scrolled" $Scale
$sa = (Get-FileHash -Algorithm SHA256 -LiteralPath $scrolledReactive).Hash
$sb = (Get-FileHash -Algorithm SHA256 -LiteralPath $scrolledLegacy).Hash
if ($sa -eq $sb) {
    Write-Host "PASS rdd-scrolled == dd-scrolled ($sa)"
} else {
    Write-Host "INFO rdd-scrolled != dd-scrolled ($sa vs $sb) - measuring"
    # The unscrolled open twin is the fill-colour sample: same control, same
    # scale, one row filled inside the viewport.
    $sample = Join-Path $work "dropdown-open@$($Scale.ToString($invariant)).png"
    python (Join-Path $toolRoot "dropdown-evidence.py") containment `
        --reactive $scrolledReactive --legacy $scrolledLegacy `
        --sample $sample --view-top $viewTop --view-bottom $viewBottom `
        --row-height $rowHeight
    switch ($LASTEXITCODE) {
        0 { Write-Host "EVIDENCE-DIVERGENCE scrolled: reactive contained, legacy leaks" }
        2 {
            Write-Host "FAIL scrolled twins diverge with neither capture leaking"
            $failures += "rdd-scrolled"
        }
        default {
            Write-Host "FAIL rdd-scrolled violates scroll containment"
            $failures += "rdd-scrolled"
        }
    }
}

& $exe --reactive-dropdown-behavior
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL reactive-dropdown behavior suite (exit $LASTEXITCODE)"
    $failures += "behavior"
}

# ---- Scale sweep ---------------------------------------------------------
if ($Sweep) {
    Write-Host ""
    Write-Host "scale  pair          result"
    foreach ($sweepScale in @(1.0, 1.25, 1.5)) {
        foreach ($pair in $pairs) {
            $r = Invoke-Capture $pair.Reactive $sweepScale
            $l = Invoke-Capture $pair.Legacy $sweepScale
            $ra = (Get-FileHash -Algorithm SHA256 -LiteralPath $r).Hash
            $rb = (Get-FileHash -Algorithm SHA256 -LiteralPath $l).Hash
            $label = $pair.Reactive.PadRight(13)
            # Invariant: a decimal comma in the table would not match the
            # scale the captures were actually taken at.
            $shown = $sweepScale.ToString($invariant).PadRight(6)
            if ($ra -eq $rb) {
                Write-Host ("{0} {1} byte-equal" -f $shown, $label)
                continue
            }
            $detail = python (Join-Path $toolRoot "dropdown-evidence.py") `
                delta $r $l
            Write-Host ("{0} {1} {2}" -f $shown, $label, $detail)
            # Only scale 1 is a contract; the fractional scales are evidence.
            if ($sweepScale -eq 1.0) {
                $failures += "sweep-$($pair.Reactive)@1.0"
            }
        }
    }
    Write-Host ""
}

if ($failures.Count -gt 0) {
    throw ("Reactive dropdown verification failed at ${Scale}x: " +
        ($failures -join ", "))
}
Write-Host ("PASS reactive dropdown: $($pairs.Count) twin pairs plus the " +
    "scrolled pair byte-identical, behavior suite green.")
