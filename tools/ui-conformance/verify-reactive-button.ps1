param(
    [double]$Scale = 1.0
)

# PBI-015 wave C: the retained reactive Button IS the text button, not a
# lookalike.
#
# 1. TWIN IDENTITY: every rbtn-X state is captured beside its btn-X twin in
#    one process and must come back BYTE-IDENTICAL. Same stage origin, same
#    pointer script, same 40 frames — only the path differs, so a single
#    differing byte is the retained runtime's own divergence.
# 2. HOVER RECONCILE: hover settled -> disabled -> pointer leaves ->
#    re-enabled must end identical to rbtn-secondary; the retained scope
#    must not replay stale hover fill across the disabled window.
# 3. BEHAVIOR: --reactive-button-behavior covers what pixels cannot —
#    activation routing, keyboard parity, queued reducer updates, and the
#    zero-allocation warm frame.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}
$work = Join-Path $toolRoot "artifacts\reactive-button"
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

$states = @(
    "secondary", "secondary-hover", "secondary-disabled",
    "disabled-unicode", "primary", "primary-hover", "primary-disabled",
    "danger", "danger-hover", "danger-disabled",
    "width-content", "width-fixed", "width-fill",
    "hover-exit", "hover-mid"
)

$failures = @()
foreach ($state in $states) {
    $reactive = Invoke-Capture "rbtn-$state" $Scale
    $legacy = Invoke-Capture "btn-$state" $Scale
    $a = (Get-FileHash -Algorithm SHA256 -LiteralPath $reactive).Hash
    $b = (Get-FileHash -Algorithm SHA256 -LiteralPath $legacy).Hash
    if ($a -eq $b) {
        Write-Host "PASS rbtn-$state == btn-$state"
    } else {
        Write-Host "FAIL rbtn-$state != btn-$state ($a vs $b)"
        $failures += "rbtn-$state"
    }
}

$idle = Invoke-Capture "rbtn-secondary" $Scale
$reconcile = Invoke-Capture "rbtn-hover-reconcile" $Scale
$idleHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $idle).Hash
$reconcileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $reconcile).Hash
if ($idleHash -eq $reconcileHash) {
    Write-Host "PASS rbtn-hover-reconcile == rbtn-secondary"
} else {
    Write-Host "FAIL rbtn-hover-reconcile != rbtn-secondary"
    $failures += "rbtn-hover-reconcile"
}

& $exe --reactive-button-behavior
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL reactive-button behavior suite (exit $LASTEXITCODE)"
    $failures += "behavior"
}

if ($failures.Count -gt 0) {
    throw ("Reactive button verification failed at ${Scale}x: " +
        ($failures -join ", "))
}
Write-Host ("PASS reactive button: $($states.Count) twin pairs " +
    "byte-identical, hover reconcile clean, behavior suite green.")
