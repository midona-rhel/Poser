param(
    [string]$Browser = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    [string[]]$Components = @(),
    [double[]]$Scales = @(1.0, 1.25, 1.5),
    [string[]]$Themes = @("dark"),
    # The ACTUAL resolved font files (run.ps1 passes the candidate
    # host's --fonts output); the browser text stack resolves the same
    # files through the Segoe UI font-link chain.
    [string[]]$FontFiles = @(),
    [switch]$ListCatalog
)

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $toolRoot "..\..")
$html = Resolve-Path (Join-Path $toolRoot "picto-reference.html")
$output = Join-Path $toolRoot "artifacts\picto"

$catalog = @(
    @{ Name = "text-label"; Width = 320; Height = 44 },
    @{ Name = "text-caption"; Width = 320; Height = 44 },
    @{ Name = "text-mono"; Width = 320; Height = 44 },
    @{ Name = "text-disabled"; Width = 320; Height = 74 },
    @{ Name = "text-truncated"; Width = 320; Height = 44 },
    @{ Name = "text-truncated-cjk"; Width = 320; Height = 44 },
    @{ Name = "text-truncated-combining"; Width = 320; Height = 44 },
    @{ Name = "text-truncated-emoji"; Width = 320; Height = 44 },
    @{ Name = "text-truncated-fit"; Width = 320; Height = 44 },
    @{ Name = "text-truncated-narrow"; Width = 320; Height = 44 },
    @{ Name = "text-truncated-flow"; Width = 320; Height = 44 },
    @{ Name = "text-wrapped"; Width = 320; Height = 96 },
    @{ Name = "text-wrapped-newline"; Width = 320; Height = 130 },
    @{ Name = "text-wrapped-overwide"; Width = 320; Height = 100 },
    @{ Name = "text-wrapped-flow"; Width = 320; Height = 130 },
    @{ Name = "text-ws-collapse"; Width = 320; Height = 80 },
    @{ Name = "text-ws-prewrap"; Width = 320; Height = 110 },
    @{ Name = "text-ws-tab"; Width = 320; Height = 74 },
    @{ Name = "text-ws-crlf"; Width = 320; Height = 130 },
    @{ Name = "text-align-end"; Width = 320; Height = 88 },
    @{ Name = "icons-grid-16"; Width = 232; Height = 256 },
    @{ Name = "icons-grid-14"; Width = 216; Height = 238 },
    @{ Name = "icons-states"; Width = 136; Height = 184 },
    @{ Name = "btn-secondary"; Width = 320; Height = 80 },
    @{ Name = "btn-secondary-hover"; Width = 320; Height = 80 },
    @{ Name = "btn-secondary-focus"; Width = 320; Height = 80 },
    @{ Name = "btn-secondary-disabled"; Width = 320; Height = 80 },
    @{ Name = "btn-primary"; Width = 320; Height = 80 },
    @{ Name = "btn-primary-hover"; Width = 320; Height = 80 },
    @{ Name = "btn-primary-focus"; Width = 320; Height = 80 },
    @{ Name = "btn-primary-disabled"; Width = 320; Height = 80 },
    @{ Name = "btn-danger"; Width = 320; Height = 80 },
    @{ Name = "btn-danger-hover"; Width = 320; Height = 80 },
    @{ Name = "btn-danger-focus"; Width = 320; Height = 80 },
    @{ Name = "btn-danger-disabled"; Width = 320; Height = 80 },
    @{ Name = "btn-width-content"; Width = 320; Height = 80 },
    @{ Name = "btn-width-fixed"; Width = 320; Height = 80 },
    @{ Name = "btn-width-fill"; Width = 320; Height = 80 },
    @{ Name = "btn-hover-exit"; Width = 320; Height = 80 },
    @{ Name = "btn-hover-mid"; Width = 320; Height = 80 },
    @{ Name = "icon-button"; Width = 120; Height = 80 },
    @{ Name = "icon-button-active"; Width = 120; Height = 80 },
    @{ Name = "switch-off"; Width = 120; Height = 80 },
    @{ Name = "switch-on"; Width = 120; Height = 80 },
    @{ Name = "text-input"; Width = 320; Height = 80 },
    @{ Name = "search-input"; Width = 320; Height = 84 },
    @{ Name = "dropdown-closed"; Width = 320; Height = 80 },
    @{ Name = "dropdown-open"; Width = 320; Height = 280 },
    @{ Name = "color-palette"; Width = 220; Height = 80 },
    @{ Name = "sidebar-row"; Width = 320; Height = 80 },
    @{ Name = "sidebar-row-selected"; Width = 320; Height = 80 },
    @{ Name = "property-row"; Width = 320; Height = 68 },
    @{ Name = "section"; Width = 320; Height = 92 },
    @{ Name = "tooltip"; Width = 240; Height = 80 },
    @{ Name = "context-menu"; Width = 320; Height = 190 },
    @{ Name = "modal"; Width = 560; Height = 360 }
)
if ($ListCatalog) {
    $catalog.Name
    return
}

# Icon fixtures are generated from the shipped source registry so the
# reference can never drift from what Poser.UI actually ships.
python (Join-Path $toolRoot "generate-icon-fixtures.py")
if ($LASTEXITCODE -ne 0) { throw "Icon fixture generation failed." }
$generatedJs = Join-Path $toolRoot "icon-fixtures.generated.js"

$variantPattern = "(?m)^\s{6}'([^']+)':\s*\{"
$variantNames = @(
    [regex]::Matches(
        (Get-Content -Raw -LiteralPath $html) + "`n" +
        (Get-Content -Raw -LiteralPath $generatedJs),
        $variantPattern) |
        ForEach-Object { $_.Groups[1].Value })
$catalogNames = @($catalog.Name | Sort-Object)
$variantNames = @($variantNames | Sort-Object)
if (Compare-Object $catalogNames $variantNames) {
    throw "Picto reference HTML and capture catalog disagree."
}

if ($Components.Count -gt 0) {
    $requested = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $Components) { [void]$requested.Add($name) }
    $catalog = @($catalog | Where-Object { $requested.Contains($_.Name) })
    if ($catalog.Count -ne $requested.Count) {
        $known = ($catalog.Name -join ", ")
        throw "Unknown reference component. Resolved set: $known"
    }
}
$knownThemes = @("dark", "light", "lightgray", "gray", "blue", "purple")
foreach ($theme in $Themes) {
    if ($knownThemes -notcontains $theme) {
        throw "Theme '$theme' is host-dependent or unknown. Pixel references support: $($knownThemes -join ', ')."
    }
}

if (!(Test-Path -LiteralPath $Browser -PathType Leaf)) {
    throw "Reference browser was not found at '$Browser'."
}

$sources = @(
    "tools\ui-conformance\picto-reference.html",
    "tools\ui-conformance\icon-fixtures.generated.js",
    "Poser.UI\Icons\TablerSvgSources.cs",
    "Poser.UI\Icons\PoserIconSources.cs",
    "..\Picto\src\shared\styles\tokens.css",
    "..\Picto\src\app\globals.css",
    "..\Picto\src\shared\styles\surfaces.css",
    "..\Picto\src\shared\styles\actionButton.module.css",
    "..\Picto\src\shared\styles\iconButton.module.css",
    "..\Picto\src\shared\ui\ToggleSwitch\ToggleSwitch.module.css",
    "..\Picto\src\shared\ui\GlassInput\GlassInput.module.css",
    "..\Picto\src\shared\ui\CmSelect\CmSelect.module.css",
    "..\Picto\src\shared\ui\CmSelect\CmSelect.tsx",
    "..\Picto\src\features\settings\Settings.tsx",
    "..\Picto\src\shared\ui\ColorPalette\ColorPalette.module.css",
    "..\Picto\src\shared\ui\SidebarRow\SidebarRow.module.css",
    "..\Picto\src\shared\ui\PropertyRow\PropertyRow.module.css",
    "..\Picto\src\shared\ui\InspectorSection\InspectorSection.module.css",
    "..\Picto\src\shared\ui\KbdTooltip\KbdTooltip.module.css",
    "..\Picto\src\shared\ui\ContextMenu\ContextMenu.module.css",
    "..\Picto\src\shared\ui\GlassModal\GlassModal.module.css",
    "..\Picto\src\shared\ui\InspectorField\InspectorField.module.css"
)

function Get-ReferenceManifest {
    @($sources | ForEach-Object {
        $source = $_
        $resolved = Resolve-Path (Join-Path $repoRoot $source)
        [ordered]@{
            path = $source.Replace("\", "/")
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolved).Hash.ToLowerInvariant()
        }
    })
}

$manifestBefore = Get-ReferenceManifest
$browserVersionBefore =
    (Get-Item -LiteralPath $Browser).VersionInfo.FileVersion
$uri = [System.Uri]::new($html.Path).AbsoluteUri
$runId = [Guid]::NewGuid().ToString("N")
$profileRoot = Join-Path $env:TEMP "poser-ui-conformance-edge-$runId"
New-Item -ItemType Directory -Force -Path $output | Out-Null
New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null

try {
    $combos = @(foreach ($theme in $Themes) {
        foreach ($scale in $Scales) {
            $suffix = [string]::Format(
                [System.Globalization.CultureInfo]::InvariantCulture,
                "{0:0.##}",
                $scale).Replace(".", "p")
            foreach ($component in $catalog) {
                [pscustomobject]@{
                    Name = $component.Name
                    Width = $component.Width
                    Height = $component.Height
                    Theme = $theme
                    Scale = $scale
                    Suffix = $suffix
                }
            }
        }
    })
    # Captures run concurrently: each already owns an isolated profile
    # and a run-unique staging file, so parallelism changes nothing about
    # isolation — only the wall clock.
    $failures = @($combos | ForEach-Object -Parallel {
        $item = $_
        $Browser = $using:Browser
        $uri = $using:uri
        $output = $using:output
        $profileRoot = $using:profileRoot
        $runId = $using:runId
        $target = Join-Path $output "$($item.Name)@$($item.Theme)@$($item.Suffix).png"
        # Capture into a run-unique staging file so a partially written
        # screenshot can never be read as the final artifact; the
        # finished file replaces it in one move.
        $staging = Join-Path $output `
            "$($item.Name)@$($item.Theme)@$($item.Suffix).partial-$runId.png"
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Force
        }
        $url = "${uri}?component=$($item.Name)&theme=$($item.Theme)"
        # Concurrent cold starts occasionally lose an Edge instance; one
        # retry with a fresh profile absorbs that without hiding a real
        # failure, which still surfaces with its reason.
        $reason = $null
        for ($attempt = 0; $attempt -lt 2; $attempt++) {
            $profile = Join-Path $profileRoot `
                "$($item.Theme)-$($item.Suffix)-$($item.Name)-$attempt"
            & $Browser `
                --headless=new `
                --disable-lcd-text `
                --hide-scrollbars `
                --disable-background-mode `
                --disable-background-networking `
                --disable-component-update `
                --disable-default-apps `
                --disable-extensions `
                --disable-sync `
                --no-first-run `
                --run-all-compositor-stages-before-draw `
                --virtual-time-budget=1000 `
                --force-device-scale-factor=$($item.Scale) `
                --window-size="$($item.Width),$($item.Height)" `
                --user-data-dir="$profile" `
                --screenshot="$staging" `
                $url | Out-Null
            if ($LASTEXITCODE -ne 0) {
                $reason = "browser exit code $LASTEXITCODE"
                continue
            }
            # The Edge launcher detaches and the real browser process
            # writes the screenshot AFTER the launcher returns. Wait for
            # the staging file to exist and stabilize — the same non-zero
            # size on two consecutive polls plus an exclusive open —
            # before promoting it.
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            $stable = $false
            $lastSize = -1
            while ([DateTime]::UtcNow -lt $deadline) {
                if (Test-Path -LiteralPath $staging) {
                    $size = (Get-Item -LiteralPath $staging).Length
                    if ($size -gt 0 -and $size -eq $lastSize) {
                        try {
                            ([IO.File]::Open(
                                $staging, 'Open', 'Read', 'None')).Dispose()
                            $stable = $true
                            break
                        } catch { }
                    }
                    $lastSize = $size
                }
                Start-Sleep -Milliseconds 150
            }
            if (-not $stable) {
                $reason = "no stable screenshot within 30s"
                continue
            }
            Move-Item -LiteralPath $staging -Destination $target -Force
            return $null
        }
        return "Reference capture failed for $($item.Name), $($item.Theme) at $($item.Scale): $reason."
    } -ThrottleLimit 6 | Where-Object { $_ })
    if ($failures.Count -gt 0) {
        throw ($failures -join "`n")
    }
}
finally {
    Get-ChildItem -LiteralPath $output -Filter "*.partial-$runId.png" `
        -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    # Every browser this run launched carries the run-unique profile root
    # in its command line; a straggler that outlives its capture belongs
    # to us and nothing else, so stop it outright instead of hoping it
    # exits inside an arbitrary wait.
    try {
        Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" `
            -ErrorAction Stop |
            Where-Object { $_.CommandLine -like "*$runId*" } |
            ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force `
                    -ErrorAction SilentlyContinue
            }
    } catch { }
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        if (Test-Path -LiteralPath $profileRoot) {
            Remove-Item -LiteralPath $profileRoot -Recurse -Force `
                -ErrorAction SilentlyContinue
        }
        if (!(Test-Path -LiteralPath $profileRoot)) {
            break
        }
    }
    if (Test-Path -LiteralPath $profileRoot) {
        throw "Edge did not release the temporary capture profile '$profileRoot'."
    }
}

$manifestAfter = Get-ReferenceManifest
$browserVersionAfter =
    (Get-Item -LiteralPath $Browser).VersionInfo.FileVersion
$beforeJson = $manifestBefore | ConvertTo-Json -Depth 4 -Compress
$afterJson = $manifestAfter | ConvertTo-Json -Depth 4 -Compress
if ($beforeJson -ne $afterJson -or
    $browserVersionBefore -ne $browserVersionAfter) {
    throw "Picto reference sources changed during capture; discard this run."
}
# The rendering environment is part of reference identity: the browser
# executable itself and the ACTUAL resolved font files (passed in by
# run.ps1 from the candidate host's resolver — the browser's Segoe UI
# font-link chain resolves the same files), plus the emoji font only
# the browser can render. A changed environment changes this manifest,
# so preserved results are marked stale.
$fontsDir = [Environment]::GetFolderPath("Fonts")
if ([string]::IsNullOrEmpty($fontsDir)) { $fontsDir = "C:\Windows\Fonts" }
$referenceFonts = @($FontFiles) + @(Join-Path $fontsDir "seguiemj.ttf") |
    ForEach-Object {
        if (Test-Path -LiteralPath $_ -PathType Leaf) {
            [ordered]@{
                path = "font:" + (Split-Path -Leaf $_)
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash.ToLowerInvariant()
            }
        }
    } | Where-Object { $_ }
$manifest = [ordered]@{
    browser = [ordered]@{
        path = $Browser
        version = $browserVersionAfter
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Browser).Hash.ToLowerInvariant()
    }
    fonts = @($referenceFonts)
    sources = $manifestAfter
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 `
    -LiteralPath (Join-Path $output "reference-sources.json")
