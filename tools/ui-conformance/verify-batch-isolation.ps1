param(
    # Cross-family states with floating chrome and pointer interaction,
    # plus every Text fixture — the states most likely to expose carried
    # ImGui state, verified across two theme/scale extremes.
    [string[]]$Components = @(
        "text-input", "dropdown-open", "tooltip", "context-menu",
        "text-label", "text-caption", "text-mono", "text-disabled",
        "text-truncated", "text-truncated-cjk", "text-truncated-combining",
        "text-truncated-emoji", "text-truncated-fit", "text-truncated-narrow",
        "text-truncated-flow", "text-wrapped", "text-wrapped-newline",
        "text-wrapped-overwide", "text-wrapped-flow", "text-ws-collapse",
        "text-ws-prewrap", "text-ws-tab", "text-ws-crlf"),
    [string[]]$Themes = @("dark", "purple"),
    [double[]]$Scales = @(1.0, 1.5)
)

# Demonstrates that per-component batch captures are pixel-identical to
# fully isolated one-process-per-capture runs: for each component, every
# (theme, scale) variant is captured both ways and the PNG hashes must
# match pairwise. Exits nonzero on any mismatch.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first (run.ps1 does, or dotnet build)."
}
$work = Join-Path $toolRoot "artifacts\batch-isolation"
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $work | Out-Null
$invariant = [Globalization.CultureInfo]::InvariantCulture

$mismatches = @()
$pairs = 0
foreach ($name in $Components) {
    $batchFile = Join-Path $work "$name-batch.txt"
    $entries = @(foreach ($theme in $Themes) {
        foreach ($scale in $Scales) {
            $suffix = [string]::Format(
                $invariant, "{0:0.##}", $scale).Replace(".", "p")
            [pscustomobject]@{
                Theme = $theme
                Scale = $scale
                Isolated = Join-Path $work "$name@$theme@$suffix.isolated.png"
                Batched = Join-Path $work "$name@$theme@$suffix.batched.png"
            }
        }
    })
    foreach ($entry in $entries) {
        & $exe $name $entry.Isolated `
            $entry.Scale.ToString($invariant) $entry.Theme | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Isolated capture failed for $name, $($entry.Theme) at $($entry.Scale)."
        }
    }
    $entries | ForEach-Object {
        "$name`t$($_.Batched)`t$($_.Scale.ToString($invariant))`t$($_.Theme)"
    } | Set-Content -Encoding utf8 -LiteralPath $batchFile
    & $exe --batch $batchFile | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Batch capture failed for $name."
    }
    foreach ($entry in $entries) {
        $pairs++
        $isolated = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $entry.Isolated).Hash
        $batched = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $entry.Batched).Hash
        if ($isolated -ne $batched) {
            $mismatches += "$name @$($entry.Theme)@$($entry.Scale)"
        }
    }
}

if ($mismatches.Count -gt 0) {
    Write-Host "BATCH ISOLATION FAILED for $($mismatches.Count)/$pairs pairs:"
    $mismatches | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "Batch isolation verified: $pairs/$pairs capture pairs hash-identical."
exit 0
