param(
    [double]$BudgetMs = 1.5
)

# The shell sidebar's WARM frame, on a 300-row tree at three depths with
# guides, badges, actor action strips and overlay toggles, fully expanded
# and never structurally changed. Two gates:
#
#   * p95 draw time under the budget — a scene with hundreds of rows costs
#     what the visible band costs, because the flat cache is rebuilt only on
#     a revision/filter/expansion change and one clipper submits the band.
#   * ZERO allocation of the sidebar's own: the same visible band painted
#     straight from the view model with no cache is measured in the same run,
#     and the sidebar must not add a byte to it. The shared painter's own
#     per-draw bytes (the SVG icon renderer dominates) are REPORTED as
#     `painter=` — they predate this view and are not its gate.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}

$output = & $exe --sidebar-perf 2>&1
$exit = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }

$line = $output | Where-Object { $_ -match "^sidebar-perf " } | Select-Object -First 1
if (-not $line) {
    throw "The sidebar perf host printed no measurement line."
}

$invariant = [Globalization.CultureInfo]::InvariantCulture
function Field([string]$name) {
    if ($line -notmatch "$name=([0-9.\-]+)") {
        throw "The measurement line carries no '$name'."
    }
    [double]::Parse($Matches[1], $invariant)
}

$p50 = Field "p50"
$p95 = Field "p95"
$max = Field "max"
$alloc = Field "alloc"
$painter = Field "painter"
$painterP50 = Field "painter-p50"

Write-Host ([string]::Format(
    $invariant,
    "Sidebar warm frame: p50={0:0.###}ms p95={1:0.###}ms max={2:0.###}ms " +
    "(the same band with no cache: {3:0.###}ms)",
    $p50, $p95, $max, $painterP50))
Write-Host ([string]::Format(
    $invariant,
    "Sidebar allocation: {0} bytes of its own; shared painter {1} bytes",
    $alloc, $painter))

$failures = @()
if ($p95 -ge $BudgetMs) {
    $failures += ("p95 {0:0.###}ms is over the {1:0.##}ms budget" -f $p95, $BudgetMs)
}
if ($alloc -ne 0) {
    $failures += ("the sidebar allocated {0} bytes of its own over the measured frames" -f $alloc)
}
if ($exit -ne 0) {
    $failures += "the capture host reported a failing case"
}
if ($failures.Count -gt 0) {
    throw ("Sidebar perf FAILED: " + ($failures -join " | "))
}

Write-Host "Sidebar perf PASS (clipped band under budget, zero sidebar allocation)"
