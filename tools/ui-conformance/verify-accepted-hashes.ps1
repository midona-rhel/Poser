# PBI-015 legacy A/B gate: compares the current candidate catalog
# (artifacts/crystarium/*.png) against the accepted hash set. Fails on any
# added, missing, or changed state, so "accepted baseline" is enforced, not
# just recorded. Run after a full default catalog capture.
# -AllowAdded: slice development adds new states; the accepted file grows only on user acceptance.
param(
    [string]$Accepted = "$PSScriptRoot\accepted-c71d682-hashes.txt",
    [switch]$AllowAdded
)
$ErrorActionPreference = "Stop"

$acceptedMap = @{}
foreach ($line in Get-Content $Accepted) {
    if ($line -notmatch '^([0-9a-f]{64})\s+\*?(.+)$') { throw "Unparseable accepted line: $line" }
    $acceptedMap[$Matches[2].Trim()] = $Matches[1]
}

$currentMap = @{}
foreach ($png in Get-ChildItem "$PSScriptRoot\artifacts\crystarium\*.png" -File) {
    $currentMap[$png.Name] = (Get-FileHash $png.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}

$added   = @($currentMap.Keys  | Where-Object { -not $acceptedMap.ContainsKey($_) } | Sort-Object)
$missing = @($acceptedMap.Keys | Where-Object { -not $currentMap.ContainsKey($_) }  | Sort-Object)
$changed = @($acceptedMap.Keys | Where-Object {
    $currentMap.ContainsKey($_) -and $currentMap[$_] -ne $acceptedMap[$_] } | Sort-Object)

foreach ($n in $added)   { "ADDED:   $n" }
foreach ($n in $missing) { "MISSING: $n" }
foreach ($n in $changed) { "CHANGED: $n" }
$fatal = $missing.Count + $changed.Count
if (-not $AllowAdded) { $fatal += $added.Count }
if ($fatal -gt 0) {
    "FAIL: $($added.Count) added, $($missing.Count) missing, $($changed.Count) changed vs accepted set."
    exit 1
}
"PASS: all $($acceptedMap.Count) candidate states match the accepted hashes" +
    $(if ($added.Count -gt 0) { " ($($added.Count) added, not yet accepted)." } else { "." })
