param(
    [double]$Scale = 1.0
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
# 2. BEHAVIOR: --reactive-dropdown-behavior covers what pixels cannot —
#    selection routing, the four ways a menu closes, keyboard parity, the
#    typed UiEvent<int> dispatch, and the warm-frame allocation gate open
#    and closed.

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

& $exe --reactive-dropdown-behavior
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL reactive-dropdown behavior suite (exit $LASTEXITCODE)"
    $failures += "behavior"
}

if ($failures.Count -gt 0) {
    throw ("Reactive dropdown verification failed at ${Scale}x: " +
        ($failures -join ", "))
}
Write-Host ("PASS reactive dropdown: $($pairs.Count) twin pairs " +
    "byte-identical, behavior suite green.")
