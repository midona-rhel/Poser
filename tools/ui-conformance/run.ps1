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
        "text-ws-prewrap", "text-ws-tab")
    "button" = @("action-button", "primary-button")
    "icon-button" = @("icon-button", "icon-button-active")
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
    throw "Unknown component '$Component'. Use all, text, combobox, button, switch, input, sidebar, or a catalog name."
}

dotnet build $project -c Debug --no-restore -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "Capture host build failed." }

$candidateCatalog = @(& $exe --list)
if ($LASTEXITCODE -ne 0) { throw "Candidate catalog query failed." }
if (Compare-Object ($all | Sort-Object) ($candidateCatalog | Sort-Object)) {
    throw "Picto reference and Crystarium candidate catalogs disagree."
}

& $captureReferences `
    -Components $components `
    -Scales $Scales `
    -Themes $Themes
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
$fontsDir = [Environment]::GetFolderPath("Fonts")
if ([string]::IsNullOrEmpty($fontsDir)) { $fontsDir = "C:\Windows\Fonts" }
$candidateFonts = @(
    "segoeui.ttf", "seguisb.ttf", "segoeuii.ttf",
    "CascadiaMono.ttf", "consola.ttf",
    "YuGothM.ttc", "YuGothB.ttc", "YuGothR.ttc", "YuGothL.ttc"
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
}) + @($candidateFonts | ForEach-Object {
    $font = Join-Path $fontsDir $_
    if (Test-Path -LiteralPath $font -PathType Leaf) {
        [ordered]@{
            path = "font:$_"
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $font).Hash.ToLowerInvariant()
        }
    }
} | Where-Object { $_ })
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

foreach ($theme in $Themes) {
    foreach ($scale in $Scales) {
        $suffix = [string]::Format(
            [Globalization.CultureInfo]::InvariantCulture,
            "{0:0.##}",
            $scale).Replace(".", "p")
        foreach ($name in $components) {
            $candidateDir = Join-Path $artifacts "crystarium"
            New-Item -ItemType Directory -Force -Path $candidateDir | Out-Null
            $candidate = Join-Path $candidateDir "$name@$theme@$suffix.png"
            & $exe $name $candidate $scale $theme
            if ($LASTEXITCODE -ne 0) {
                throw "Crystarium capture failed for $name, $theme at $scale."
            }

            $reference = Join-Path $artifacts "picto\$name@$theme@$suffix.png"
            $result = Join-Path $results "$theme\$name\$suffix"
            python (Join-Path $toolRoot "compare.py") `
                --reference $reference `
                --candidate $candidate `
                --output $result `
                --component "$name [$theme]" `
                --scale $scale `
                --reference-manifest-hash $referenceManifestHash `
                --candidate-hash $candidateHash `
                --candidate-commit $candidateCommit `
                --candidate-dirty $candidateDirty
            if ($LASTEXITCODE -ne 0) {
                throw "Comparison failed for $name, $theme at $scale."
            }
        }
    }
}

python (Join-Path $toolRoot "compare.py") `
    --aggregate $artifacts `
    --reference-manifest-hash $referenceManifestHash `
    --candidate-hash $candidateHash
if ($LASTEXITCODE -ne 0) { throw "Aggregate report failed." }
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
