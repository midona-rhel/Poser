param(
    [double]$Scale = 1.0
)

# PBI-015 wave O: the retained SearchPicker.
#
# This one is NOT a byte gate, and that is the point. Waves H and L held the
# reactive button and dropdown to SHA-identity with their imperative twins
# because those were transcriptions: same control, same pixels, different path.
# The picker is a REDESIGN — its panel adopts Picto's OverlayShell (the 40px
# .header, the 36px .searchArea/.searchRow, the 28px .checkRow list) in place of
# the imperative picker's own chrome — so asserting equality with picker-open
# would assert the redesign did not happen.
#
# What replaces it:
#
# 1. BEHAVIOR (--reactive-picker-behavior) carries the contract, and carries
#    more of it than before because pixels carry less: the surface opening on
#    its trigger, typed characters reaching the component's own query through
#    the native filter island, a single-select row dispatching its ITEM once and
#    closing, a multi-select row toggling WITHOUT closing, toggles accumulating,
#    dismissal keeping the caller's selection (the multi variant is controlled —
#    it reports flips and stores nothing), and the warm-frame allocation gate.
#
# 2. PIXELS are judged against the PICTO reference cell by the composed sheet,
#    not here: run.ps1 reports each state's significant%, and rpicker-multi is
#    the closer of the two because OverlayShell's .checkRow markup literally
#    describes it. This script asserts only that both states render, compose and
#    are not blank — the kind of failure a script can name. The VISUAL VERDICT
#    IS THE USER'S, at the Phase 3A review; nothing here approves the design.
#
# 3. The surface BOX is unchanged from the imperative picker's token arithmetic,
#    so the panel still clamps to the same place inside the 320x280 cell and one
#    reference cell judges all three candidates.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}
$work = Join-Path $toolRoot "artifacts\reactive-picker"
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $work | Out-Null
$invariant = [Globalization.CultureInfo]::InvariantCulture

$failures = @()

# ---- Render check --------------------------------------------------------
# A capture that threw, composed nothing, or came back a flat field would all
# reach the sheet as a silently wrong cell. Distinct pixel values is the
# cheapest statement that separates "drew the panel" from "drew a rectangle".
foreach ($state in @("rpicker-open", "rpicker-multi")) {
    $png = Join-Path $work "$state@$($Scale.ToString($invariant)).png"
    & $exe $state $png $Scale.ToString($invariant) dark | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL $state capture failed"
        $failures += $state
        continue
    }
    if (!(Test-Path -LiteralPath $png -PathType Leaf)) {
        Write-Host "FAIL $state produced no file"
        $failures += $state
        continue
    }
    # A PNG of a flat field compresses to almost nothing, so the encoded size
    # separates "drew the panel" from "drew a rectangle" without a decoder.
    $bytes = (Get-Item -LiteralPath $png).Length
    if ($bytes -lt 2000) {
        Write-Host "FAIL $state rendered blank ($bytes bytes)"
        $failures += $state
    } else {
        Write-Host "PASS $state renders ($bytes bytes)"
    }
}

# ---- Two states must DIFFER ----------------------------------------------
# Single-select trades .checkBox for a bare tick, so the two variants cannot be
# the same image. Equal hashes would mean one fixture is drawing the other.
$single = Join-Path $work "rpicker-open@$($Scale.ToString($invariant)).png"
$multi = Join-Path $work "rpicker-multi@$($Scale.ToString($invariant)).png"
if ((Test-Path -LiteralPath $single) -and (Test-Path -LiteralPath $multi)) {
    $a = (Get-FileHash -Algorithm SHA256 -LiteralPath $single).Hash
    $b = (Get-FileHash -Algorithm SHA256 -LiteralPath $multi).Hash
    if ($a -eq $b) {
        Write-Host "FAIL rpicker-open and rpicker-multi are the same image"
        $failures += "variants-identical"
    } else {
        Write-Host "PASS rpicker-open != rpicker-multi (the check slot differs)"
    }
}

# ---- The redesign actually happened --------------------------------------
# rpicker-open must NOT equal picker-open. Wave O's whole premise is that the
# retained picker stopped being a transcription; an equal hash would mean the
# OverlayShell chrome never landed.
$legacy = Join-Path $work "picker-open@$($Scale.ToString($invariant)).png"
& $exe picker-open $legacy $Scale.ToString($invariant) dark | Out-Null
if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $single)) {
    $a = (Get-FileHash -Algorithm SHA256 -LiteralPath $single).Hash
    $b = (Get-FileHash -Algorithm SHA256 -LiteralPath $legacy).Hash
    if ($a -eq $b) {
        Write-Host "FAIL rpicker-open is byte-identical to picker-open"
        $failures += "redesign-missing"
    } else {
        Write-Host "PASS rpicker-open != picker-open (the redesign is present)"
    }
}

# ---- Behavior ------------------------------------------------------------
& $exe --reactive-picker-behavior
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL reactive-picker behavior suite (exit $LASTEXITCODE)"
    $failures += "behavior"
}

if ($failures.Count -gt 0) {
    throw ("Reactive picker verification failed at ${Scale}x: " +
        ($failures -join ", "))
}
Write-Host ("PASS reactive picker: both states render and differ, the " +
    "redesign is present, behavior suite green. Pixel fidelity against Picto " +
    "is reported by run.ps1 and judged at the Phase 3A review.")
