param(
    [Parameter(Position = 0)]
    [string]$Component = "all",
    [double[]]$Scales = @(1.0, 1.25, 1.5),
    [string[]]$Themes = @(
        "dark", "light", "lightgray", "gray", "blue", "purple"),
    [switch]$Clean,
    [switch]$OpenReport
)

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $toolRoot "..\..")
$project = Join-Path $toolRoot "Crystarium.Capture\Crystarium.Capture.csproj"
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
$artifacts = Join-Path $toolRoot "artifacts"
$results = Join-Path $artifacts "results"
$artifactsFull = [IO.Path]::GetFullPath($artifacts).TrimEnd(
    [IO.Path]::DirectorySeparatorChar)
$resultsFull = [IO.Path]::GetFullPath($results)
if (!$resultsFull.StartsWith(
        "$artifactsFull$([IO.Path]::DirectorySeparatorChar)",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a result path outside the artifact directory."
}
if ($Clean -and (Test-Path -LiteralPath $resultsFull)) {
    Remove-Item -LiteralPath $resultsFull -Recurse -Force
}

$captureReferences = Join-Path $toolRoot "capture-picto-references.ps1"
$all = @(& $captureReferences -ListCatalog)
$aliases = @{
    "text" = @(
        "text-label", "text-caption", "text-mono", "text-disabled",
        "text-truncated", "text-truncated-cjk", "text-truncated-combining",
        "text-truncated-emoji", "text-truncated-fit", "text-truncated-narrow",
        "text-truncated-flow", "text-wrapped", "text-wrapped-newline",
        "text-wrapped-overwide", "text-wrapped-flow", "text-ws-collapse",
        "text-ws-prewrap", "text-ws-tab", "text-ws-crlf", "text-align-end")
    "icons" = @("icons-grid-16", "icons-grid-14", "icons-states")
    "text-buttons" = @(
        "btn-secondary",
        "btn-secondary-hover",
        "btn-secondary-focus",
        "btn-secondary-disabled",
        "btn-disabled-unicode",
        "btn-primary",
        "btn-primary-hover",
        "btn-primary-focus",
        "btn-primary-disabled",
        "btn-danger",
        "btn-danger-hover",
        "btn-danger-focus",
        "btn-danger-disabled",
        "btn-width-content",
        "btn-width-fixed",
        "btn-width-fill",
        "btn-hover-exit",
        "btn-hover-mid")
    "icon-button" = @(
        "icon-button-idle",
        "icon-button-hover",
        "icon-button-pressed",
        "icon-button-held-outside",
        "icon-button-disabled",
        "icon-button-hover-mid",
        "icon-button-hover-exit",
        "icon-button-keyboard-focused",
        "icon-button-glyphs")
    "switch" = @("switch-off", "switch-on")
    "input" = @("text-input", "search-input")
    "combobox" = @("dropdown-closed", "dropdown-open")
    "dropdown" = @("dropdown-closed", "dropdown-open")
    "sidebar" = @("sidebar-row", "sidebar-row-selected")
}
$components = if ($Component -eq "all") {
    $all
} elseif ($aliases.ContainsKey($Component)) {
    $aliases[$Component]
} elseif ($all -contains $Component) {
    @($Component)
} else {
    throw "Unknown component '$Component'. Use all, text, icons, text-buttons, combobox, switch, input, sidebar, or a catalog name."
}

dotnet build $project -c Debug --no-restore -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "Capture host build failed." }

$candidateCatalog = @(& $exe --list)
if ($LASTEXITCODE -ne 0) { throw "Candidate catalog query failed." }
if (Compare-Object ($all | Sort-Object) ($candidateCatalog | Sort-Object)) {
    throw "Picto reference and Crystarium candidate catalogs disagree."
}

# The icon reference is generated from the shipped registry; assert the
# ordered name lists agree so neither side can silently drift.
python (Join-Path $toolRoot "generate-icon-fixtures.py")
if ($LASTEXITCODE -ne 0) { throw "Icon fixture generation failed." }
$referenceIconNames = @(Get-Content -LiteralPath (
    Join-Path $toolRoot "icon-names.generated.txt"))
$candidateIconNames = @(& $exe --icons)
if ($LASTEXITCODE -ne 0) { throw "Candidate icon name query failed." }
if (Compare-Object $referenceIconNames $candidateIconNames -SyncWindow 0) {
    throw "Generated icon reference and Tabler.ShippedNames disagree in content or order."
}

# The candidate host reports the exact font files this machine's
# registry resolves — base faces plus the shared font-link CJK fallback
# — so BOTH manifests hash real resolutions, never an assumed list.
$resolvedFonts = @(& $exe --fonts)
if ($LASTEXITCODE -ne 0) { throw "Candidate font resolution failed." }

& $captureReferences `
    -Components $components `
    -Scales $Scales `
    -Themes $Themes `
    -FontFiles $resolvedFonts
if ($LASTEXITCODE -ne 0) { throw "Picto reference capture failed." }

$referenceManifest = Join-Path $artifacts "picto\reference-sources.json"
$referenceManifestHash = (
    Get-FileHash -Algorithm SHA256 -LiteralPath $referenceManifest
).Hash.ToLowerInvariant()
# Provenance hashes the deterministic rendering binaries that actually
# produce the candidate pixels — the apphost .exe alone would miss a
# rebuilt Poser.UI.dll entirely — plus every font file the candidate can
# resolve (FontRegistry faces and the Windows Japanese UI fallback), so
# preserved results go stale when the rendering environment changes. The
# ordered manifest is written next to the captures and its own hash is
# the candidate identity.
$binDir = Split-Path -Parent $exe
$candidateBinaries = @(
    "Poser.UI.dll",
    "Crystarium.Capture.dll",
    "Dalamud.Bindings.ImGui.dll",
    "cimgui.dll"
)
$candidateManifest = @($candidateBinaries | ForEach-Object {
    $binary = Join-Path $binDir $_
    if (!(Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "Candidate rendering binary '$_' was not found in '$binDir'."
    }
    [ordered]@{
        path = $_
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $binary).Hash.ToLowerInvariant()
    }
}) + @($resolvedFonts | ForEach-Object {
    [ordered]@{
        path = "font:" + (Split-Path -Leaf $_)
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash.ToLowerInvariant()
    }
})
$candidateDir = Join-Path $artifacts "crystarium"
New-Item -ItemType Directory -Force -Path $candidateDir | Out-Null
$candidateManifestPath = Join-Path $candidateDir "candidate-manifest.json"
$candidateManifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 `
    -LiteralPath $candidateManifestPath
$candidateHash = (
    Get-FileHash -Algorithm SHA256 -LiteralPath $candidateManifestPath
).Hash.ToLowerInvariant()
$candidateCommit = (git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Could not resolve the candidate commit." }
$candidateDirty = [bool](git -C $repoRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw "Could not inspect candidate worktree state." }

# Candidate captures share one process, D3D device, and font atlas. The
# capture host creates and destroys a fresh ImGui context for EVERY entry,
# so interaction, timing, and widget state remain isolated across component
# boundaries without paying process and atlas startup once per component.
$invariant = [Globalization.CultureInfo]::InvariantCulture
$combos = @(foreach ($theme in $Themes) {
    foreach ($scale in $Scales) {
        $suffix = [string]::Format(
            $invariant, "{0:0.##}", $scale).Replace(".", "p")
        foreach ($name in $components) {
            [pscustomobject]@{
                Name = $name
                Theme = $theme
                Scale = $scale
                Suffix = $suffix
                Candidate = Join-Path $candidateDir "$name@$theme@$suffix.png"
            }
        }
    }
})
$batchFile = Join-Path $artifacts "candidate-batch.txt"
$combos | ForEach-Object {
    "$($_.Name)`t$($_.Candidate)`t$($_.Scale.ToString($invariant))`t$($_.Theme)"
} | Set-Content -Encoding utf8 -LiteralPath $batchFile
& $exe --batch $batchFile
if ($LASTEXITCODE -ne 0) {
    throw "Crystarium batch capture failed."
}

$comparisonBatch = Join-Path $artifacts "comparison-batch.json"
@($combos | ForEach-Object {
    [ordered]@{
        reference = Join-Path $artifacts `
            "picto\$($_.Name)@$($_.Theme)@$($_.Suffix).png"
        candidate = $_.Candidate
        output = Join-Path $results `
            "$($_.Theme)\$($_.Name)\$($_.Suffix)"
        component = "$($_.Name) [$($_.Theme)]"
        scale = $_.Scale.ToString($invariant)
        referenceManifestHash = $referenceManifestHash
        candidateHash = $candidateHash
        candidateCommit = $candidateCommit
        candidateDirty = $candidateDirty
    }
}) | ConvertTo-Json -Depth 4 -AsArray | Set-Content -Encoding utf8 `
    -LiteralPath $comparisonBatch
python (Join-Path $toolRoot "compare.py") `
    --batch $comparisonBatch `
    --aggregate $artifacts `
    --reference-manifest-hash $referenceManifestHash `
    --candidate-hash $candidateHash
if ($LASTEXITCODE -ne 0) { throw "Comparison or aggregate report failed." }
$report = Join-Path $artifacts "index.html"
Write-Host "UI conformance report: $report"
if ($OpenReport) {
    $edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    if (Test-Path -LiteralPath $edge) {
        $reportUri = [Uri]::new((Resolve-Path $report).Path).AbsoluteUri
        Start-Process $edge -ArgumentList "--app=$reportUri", "--start-maximized"
    } else {
        Start-Process $report
    }
}
