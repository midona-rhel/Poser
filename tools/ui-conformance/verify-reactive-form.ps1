param(
    [double]$Scale = 1.0
)

# PBI-015 wave P: the retained form system IS the imperative one.
#
# 1. TWIN IDENTITY. Every reactive form leaf rides its imperative twin's own
#    wave-M paint seam — PaintSlider, PaintSwitch, PaintColorWellBox,
#    PaintProgress, PaintSectionRule, PaintSectionHeader — so the twin owns
#    the box, the state and the dispatch and contributes no pixels of its
#    own. Each rX state is therefore captured beside its legacy twin in one
#    process and must come back BYTE-IDENTICAL; a differing byte is the
#    retained runtime's layout or rounding, never the control's look.
#
#    The SLIDER pair is the first capture taken since the fill was recoloured
#    white (Slider.cs, user decision 2026-08-02). Both sides read the same
#    seam, so identity is what the recolour predicts. If that pair alone
#    fails, the recolour is not the suspect: the two paths reached different
#    BOXES, and the pixel diff says which side moved.
#
# 2. The SECTION twins carry no content, because the legacy fixture they are
#    gated against carries none: Ui.Section is handed an empty FormScope
#    body. Rows on the reactive side would be pixels the comparison has no
#    counterpart for, so the composed twin is rule + header exactly.
#
# 3. BEHAVIOR: --reactive-form-behavior covers what pixels cannot — the drag
#    that reports per move and stops at the release, the controlled toggle,
#    the popup a path-derived id opens, the disclosure's next-frame content
#    and still-running chevron, the readout's one-frame controlled lag, the
#    permanent-but-empty reset slot, and the warm-frame allocation ceiling.
#
# Pixel fidelity against Picto is reported by run.ps1 per state and judged at
# the Phase 3A review; nothing here approves a look.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}
$work = Join-Path $toolRoot "artifacts\reactive-form"
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
    @{ Reactive = "rslider";             Legacy = "slider" },
    @{ Reactive = "rslider-disabled";    Legacy = "slider-disabled" },
    @{ Reactive = "rcolorwell";          Legacy = "colorwell" },
    @{ Reactive = "rcolorwell-disabled"; Legacy = "colorwell-disabled" },
    @{ Reactive = "rprogress";           Legacy = "progress" },
    @{ Reactive = "rswitch-off";         Legacy = "switch-off" },
    @{ Reactive = "rswitch-on";          Legacy = "switch-on" },
    @{ Reactive = "rsection";            Legacy = "section" },
    @{ Reactive = "rsection-expanded";   Legacy = "section-expanded" },
    @{ Reactive = "rsection-hover";      Legacy = "section-hover" }
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

# ---- The three section states must DIFFER from each other -----------------
# Collapsed, expanded and header-hover draw the SAME call; only `expanded`
# and the parked pointer differ. Equal hashes would mean the disclosure state
# never reached the paint at all, and the byte gate above would still pass
# because the legacy twin would be equally wrong.
$sectionStates = @("rsection", "rsection-expanded", "rsection-hover")
$sectionHashes = @{}
foreach ($state in $sectionStates) {
    $png = Join-Path $work "$state@$($Scale.ToString($invariant)).png"
    $sectionHashes[$state] = (
        Get-FileHash -Algorithm SHA256 -LiteralPath $png).Hash
}
$distinct = @($sectionHashes.Values | Sort-Object -Unique).Count
if ($distinct -eq $sectionStates.Count) {
    Write-Host "PASS the three reactive section states are three images"
} else {
    Write-Host "FAIL reactive section states collapse to $distinct image(s)"
    $failures += "section-states-identical"
}

# ---- Behavior -------------------------------------------------------------
& $exe --reactive-form-behavior
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL reactive-form behavior suite (exit $LASTEXITCODE)"
    $failures += "behavior"
}

if ($failures.Count -gt 0) {
    throw ("Reactive form verification failed at ${Scale}x: " +
        ($failures -join ", "))
}
Write-Host ("PASS reactive form: $($pairs.Count) twin pairs byte-identical, " +
    "the section states distinct, behavior suite green.")
