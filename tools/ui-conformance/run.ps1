param(
    [Parameter(Position = 0)]
    [string]$Component = "all",
    # Dark @ 100% is the default quick run. There is no default
    # theme/scale matrix: scale sweeps (geometry changes) and non-dark
    # themes (compositing changes) are explicit diagnostics.
    [double[]]$Scales = @(1.0),
    [string[]]$Themes = @("dark"),
    [string]$Browser = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
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
$invariant = [Globalization.CultureInfo]::InvariantCulture

$knownThemes = @("dark", "light", "lightgray", "gray", "blue", "purple")
foreach ($theme in $Themes) {
    if ($knownThemes -notcontains $theme) {
        throw "Theme '$theme' is host-dependent or unknown. Sheets support: $($knownThemes -join ', ')."
    }
}

$catalog = (Get-Content -Raw -LiteralPath (
    Join-Path $toolRoot "sheet-catalog.json") | ConvertFrom-Json).components
$componentNames = @($catalog.name)
$sheets = if ($Component -eq "all") {
    $componentNames
} elseif ($componentNames -contains $Component) {
    @($Component)
} else {
    throw "Unknown component '$Component'. Sheets: all, $($componentNames -join ', ')."
}
$states = @($catalog | Where-Object { $sheets -contains $_.name } |
    ForEach-Object { $_.states } | ForEach-Object { $_.name })
$allStates = @($catalog.states.name)

if ($Clean -and (Test-Path -LiteralPath $artifacts)) {
    Remove-Item -LiteralPath $artifacts -Recurse -Force
}

# Compilation is excluded from the timed run (the performance gate
# covers capture + composition of the warm default catalog).
dotnet build $project -c Debug --no-restore -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "Capture host build failed." }

$stopwatch = [Diagnostics.Stopwatch]::StartNew()

# ── Catalog agreement: both sides must render exactly the states the
# sheet catalog declares. ──
$candidateCatalog = @(& $exe --list)
if ($LASTEXITCODE -ne 0) { throw "Candidate catalog query failed." }
if (Compare-Object ($allStates | Sort-Object) ($candidateCatalog | Sort-Object)) {
    throw "sheet-catalog.json and the Crystarium candidate catalog disagree."
}
python (Join-Path $toolRoot "generate-icon-fixtures.py")
if ($LASTEXITCODE -ne 0) { throw "Icon fixture generation failed." }
$referenceIconNames = @(Get-Content -LiteralPath (
    Join-Path $toolRoot "icon-names.generated.txt"))
$candidateIconNames = @(& $exe --icons)
if ($LASTEXITCODE -ne 0) { throw "Candidate icon name query failed." }
if (Compare-Object $referenceIconNames $candidateIconNames -SyncWindow 0) {
    throw "Generated icon reference and Tabler.ShippedNames disagree in content or order."
}

# ── Page layout: one implementation (sheets.py) positions the cells for
# the reference page and the compositor alike. ──
python (Join-Path $toolRoot "sheets.py") --layout
if ($LASTEXITCODE -ne 0) { throw "Sheet layout generation failed." }
$layoutJs = Get-Content -Raw -LiteralPath (
    Join-Path $toolRoot "sheet-layout.generated.js")
$layout = ($layoutJs -replace "(?s)^.*?=\s*", "" -replace ";\s*$", "") |
    ConvertFrom-Json

# ── Reference identity: the browser, every reference source, the fonts,
# and the layout. Unchanged identity + existing captures = warm reuse. ──
$resolvedFonts = @(& $exe --fonts)
if ($LASTEXITCODE -ne 0) { throw "Candidate font resolution failed." }
$sources = @(
    "tools\ui-conformance\picto-reference.html",
    "tools\ui-conformance\sheet-catalog.json",
    "tools\ui-conformance\sheet-layout.generated.js",
    "tools\ui-conformance\icon-fixtures.generated.js",
    "Poser.UI\Icons\TablerSvgSources.cs",
    "Poser.UI\Icons\PoserIconSources.cs",
    "..\Picto\src\shared\styles\tokens.css",
    "..\Picto\src\app\globals.css",
    "..\Picto\src\shared\styles\surfaces.css",
    "..\Picto\src\shared\styles\actionButton.module.css",
    "..\Picto\src\shared\styles\iconButton.module.css",
    "..\Picto\src\app\AppShell.module.css",
    "..\Picto\src\app\AppShell.tsx",
    "..\Picto\src\shared\ui\ToggleSwitch\ToggleSwitch.module.css",
    "..\Picto\src\shared\ui\GlassInput\GlassInput.module.css",
    "..\Picto\src\shared\ui\CmSelect\CmSelect.module.css",
    "..\Picto\src\shared\ui\ColorPalette\ColorPalette.module.css",
    "..\Picto\src\shared\ui\SidebarRow\SidebarRow.module.css",
    "..\Picto\src\shared\ui\PropertyRow\PropertyRow.module.css",
    "..\Picto\src\shared\ui\InspectorSection\InspectorSection.module.css",
    "..\Picto\src\shared\ui\KbdTooltip\KbdTooltip.module.css",
    "..\Picto\src\shared\ui\ContextMenu\ContextMenu.module.css",
    "..\Picto\src\shared\ui\GlassModal\GlassModal.module.css",
    "..\Picto\src\shared\ui\InspectorField\InspectorField.module.css"
)
if (!(Test-Path -LiteralPath $Browser -PathType Leaf)) {
    throw "Reference browser was not found at '$Browser'."
}
$fontsDir = [Environment]::GetFolderPath("Fonts")
if ([string]::IsNullOrEmpty($fontsDir)) { $fontsDir = "C:\Windows\Fonts" }
$manifest = [ordered]@{
    browser = [ordered]@{
        path = $Browser
        version = (Get-Item -LiteralPath $Browser).VersionInfo.FileVersion
        sha256 = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $Browser).Hash.ToLowerInvariant()
    }
    fonts = @(@($resolvedFonts) + @(Join-Path $fontsDir "seguiemj.ttf") |
        ForEach-Object {
            if (Test-Path -LiteralPath $_ -PathType Leaf) {
                [ordered]@{
                    path = "font:" + (Split-Path -Leaf $_)
                    sha256 = (Get-FileHash -Algorithm SHA256 `
                        -LiteralPath $_).Hash.ToLowerInvariant()
                }
            }
        } | Where-Object { $_ })
    sources = @($sources | ForEach-Object {
        [ordered]@{
            path = $_.Replace("\", "/")
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (
                Resolve-Path (Join-Path $repoRoot $_))).Hash.ToLowerInvariant()
        }
    })
}
$pictoDir = Join-Path $artifacts "picto"
New-Item -ItemType Directory -Force -Path $pictoDir | Out-Null
$manifestPath = Join-Path $pictoDir "reference-sources.json"
$manifestJson = $manifest | ConvertTo-Json -Depth 6
$identityCurrent = (Test-Path -LiteralPath $manifestPath) -and (
    (Get-Content -Raw -LiteralPath $manifestPath).TrimEnd() -eq
    $manifestJson.TrimEnd())

$combos = @(foreach ($theme in $Themes) {
    foreach ($scale in $Scales) {
        [pscustomobject]@{
            Theme = $theme
            Scale = $scale
            Suffix = [string]::Format(
                $invariant, "{0:0.##}", $scale).Replace(".", "p")
        }
    }
})

# ── Reference capture: ONE Edge process renders the whole catalog page
# per combo (the default run is a single process and screenshot). ──
$uri = [Uri]::new((Resolve-Path (
    Join-Path $toolRoot "picto-reference.html")).Path).AbsoluteUri
$runId = [Guid]::NewGuid().ToString("N")
$profileRoot = Join-Path $env:TEMP "poser-ui-sheets-edge-$runId"
try {
    foreach ($combo in $combos) {
        $target = Join-Path $pictoDir `
            "catalog@$($combo.Theme)@$($combo.Suffix).png"
        if ($identityCurrent -and (Test-Path -LiteralPath $target)) {
            Write-Host "reference current: catalog@$($combo.Theme)@$($combo.Suffix)"
            continue
        }
        $staging = "$target.partial-$runId.png"
        $reason = $null
        $captured = $false
        for ($attempt = 0; $attempt -lt 2 -and -not $captured; $attempt++) {
            $profile = Join-Path $profileRoot `
                "$($combo.Theme)-$($combo.Suffix)-$attempt"
            New-Item -ItemType Directory -Force -Path $profile | Out-Null
            # The Edge launcher detaches; the real browser writes the
            # screenshot after it returns. Wait for the staging file to
            # stabilize before promoting it.
            $PSNativeCommandUseErrorActionPreference = $false
            Start-Process -FilePath $Browser -WindowStyle Hidden -ArgumentList @(
                "--headless=new",
                "--disable-lcd-text",
                "--hide-scrollbars",
                "--disable-background-mode",
                "--disable-background-networking",
                "--disable-component-update",
                "--disable-default-apps",
                "--disable-extensions",
                "--disable-sync",
                "--no-first-run",
                "--run-all-compositor-stages-before-draw",
                "--virtual-time-budget=2000",
                "--force-device-scale-factor=$($combo.Scale.ToString($invariant))",
                "--window-size=$($layout.pageWidth),$($layout.pageHeight)",
                "--user-data-dir=$profile",
                "--screenshot=$staging",
                "${uri}?theme=$($combo.Theme)") | Out-Null
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            $lastSize = -1
            while ([DateTime]::UtcNow -lt $deadline) {
                if (Test-Path -LiteralPath $staging) {
                    $size = (Get-Item -LiteralPath $staging).Length
                    if ($size -gt 0 -and $size -eq $lastSize) {
                        try {
                            ([IO.File]::Open(
                                $staging, 'Open', 'Read', 'None')).Dispose()
                            $captured = $true
                            break
                        } catch { }
                    }
                    $lastSize = $size
                }
                Start-Sleep -Milliseconds 150
            }
            if (-not $captured) { $reason = "no stable screenshot within 45s" }
        }
        if (-not $captured) {
            throw "Reference catalog capture failed for $($combo.Theme)@$($combo.Suffix): $reason."
        }
        Move-Item -LiteralPath $staging -Destination $target -Force
    }
}
finally {
    Get-ChildItem -LiteralPath $pictoDir -Filter "*.partial-$runId.png" `
        -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    try {
        Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" `
            -ErrorAction Stop |
            Where-Object { $_.CommandLine -like "*$runId*" } |
            ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force `
                    -ErrorAction SilentlyContinue
            }
    } catch { }
    if (Test-Path -LiteralPath $profileRoot) {
        Remove-Item -LiteralPath $profileRoot -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
}
$manifestJson | Set-Content -Encoding utf8 -LiteralPath $manifestPath
$referenceHash = (Get-FileHash -Algorithm SHA256 `
    -LiteralPath $manifestPath).Hash.ToLowerInvariant()

# ── Candidate capture: ONE process, sequential per-state contexts with
# real pointer, keyboard, and frame timing per state. ──
$candidateDir = Join-Path $artifacts "crystarium"
New-Item -ItemType Directory -Force -Path $candidateDir | Out-Null
$batchFile = Join-Path $artifacts "candidate-batch.txt"
@(foreach ($combo in $combos) {
    foreach ($state in $states) {
        $png = Join-Path $candidateDir "$state@$($combo.Theme)@$($combo.Suffix).png"
        "$state`t$png`t$($combo.Scale.ToString($invariant))`t$($combo.Theme)"
    }
}) | Set-Content -Encoding utf8 -LiteralPath $batchFile
& $exe --batch $batchFile
if ($LASTEXITCODE -ne 0) { throw "Crystarium batch capture failed." }

$binDir = Split-Path -Parent $exe
$candidateManifest = @(@(
    "Poser.UI.dll",
    "Crystarium.Capture.dll",
    "Dalamud.Bindings.ImGui.dll",
    "cimgui.dll"
) | ForEach-Object {
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
$candidateManifestPath = Join-Path $candidateDir "candidate-manifest.json"
$candidateManifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 `
    -LiteralPath $candidateManifestPath
$candidateHash = (Get-FileHash -Algorithm SHA256 `
    -LiteralPath $candidateManifestPath).Hash.ToLowerInvariant()
$candidateCommit = (git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Could not resolve the candidate commit." }
$candidateDirty = [bool](git -C $repoRoot status --porcelain)

# ── Composition: labeled per-component sheets, red diff, window. ──
foreach ($combo in $combos) {
    $composeArgs = @(
        (Join-Path $toolRoot "sheets.py"), "--compose",
        "--theme", $combo.Theme,
        "--scale", $combo.Scale.ToString($invariant),
        "--suffix", $combo.Suffix,
        "--artifacts", $artifacts,
        "--commit", $candidateCommit,
        "--dirty", ($candidateDirty ? "true" : "false"),
        "--reference-hash", $referenceHash,
        "--candidate-hash", $candidateHash)
    if ($Component -ne "all") {
        $composeArgs += @("--components", ($sheets -join ","))
    }
    python @composeArgs
    if ($LASTEXITCODE -ne 0) { throw "Sheet composition failed." }
}

$stopwatch.Stop()
$elapsed = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
Write-Host "Sheet run: ${elapsed}s (excluding compilation)."
$isDefaultRun = $Component -eq "all" -and
    $Themes.Count -eq 1 -and $Themes[0] -eq "dark" -and
    $Scales.Count -eq 1 -and $Scales[0] -eq 1.0
if ($isDefaultRun) {
    if ($elapsed -gt 60) {
        throw "Performance gate: default catalog run took ${elapsed}s (hard gate 60s)."
    }
    if ($elapsed -gt 30) {
        Write-Warning "Default catalog run took ${elapsed}s (target 30s)."
    }
}

$report = Join-Path $artifacts "index.html"
Write-Host "Comparison window: $report"
if ($OpenReport) {
    if (Test-Path -LiteralPath $Browser) {
        $reportUri = [Uri]::new((Resolve-Path $report).Path).AbsoluteUri
        Start-Process $Browser -ArgumentList "--app=$reportUri", "--start-maximized"
    } else {
        Start-Process $report
    }
}
