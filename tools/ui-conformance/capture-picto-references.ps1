param(
    [string]$Browser = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    [string[]]$Components = @(),
    [double[]]$Scales = @(1.0, 1.25, 1.5),
    [string[]]$Themes = @("dark")
)

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $toolRoot "..\..")
$html = Resolve-Path (Join-Path $toolRoot "picto-reference.html")
$output = Join-Path $toolRoot "artifacts\picto"
$runId = [Guid]::NewGuid().ToString("N")
$profileRoot = Join-Path $env:TEMP "poser-ui-conformance-edge-$runId"

New-Item -ItemType Directory -Force -Path $output | Out-Null
New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null

$catalog = @(
    @{ Name = "action-button"; Width = 320; Height = 80 },
    @{ Name = "primary-button"; Width = 320; Height = 80 },
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
$uri = [System.Uri]::new($html.Path).AbsoluteUri
$knownThemes = @("dark", "light", "lightgray", "gray", "blue", "purple")
foreach ($theme in $Themes) {
    if ($knownThemes -notcontains $theme) {
        throw "Theme '$theme' is host-dependent or unknown. Pixel references support: $($knownThemes -join ', ')."
    }
}

foreach ($theme in $Themes) {
    foreach ($scale in $Scales) {
        $suffix = [string]::Format(
            [System.Globalization.CultureInfo]::InvariantCulture,
            "{0:0.##}",
            $scale).Replace(".", "p")
        foreach ($component in $catalog) {
            $target = Join-Path $output "$($component.Name)@$theme@$suffix.png"
            $url = "${uri}?component=$($component.Name)&theme=$theme"
            $profile = Join-Path $profileRoot "$theme-$suffix-$($component.Name)"
            & $Browser `
                --headless=new `
                --hide-scrollbars `
                --run-all-compositor-stages-before-draw `
                --virtual-time-budget=1000 `
                --force-device-scale-factor=$scale `
                --window-size="$($component.Width),$($component.Height)" `
                --user-data-dir="$profile" `
                --screenshot="$target" `
                $url | Out-Null
            if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $target)) {
                throw "Reference capture failed for $($component.Name), $theme at $scale."
            }
        }
    }
}

$sources = @(
    "..\Picto\src\shared\styles\tokens.css",
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
    "..\Picto\src\shared\ui\GlassModal\GlassModal.module.css"
)
$manifest = foreach ($source in $sources) {
    $resolved = Resolve-Path (Join-Path $repoRoot $source)
    @{
        path = $source.Replace("\", "/")
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolved).Hash.ToLowerInvariant()
    }
}
$manifest | ConvertTo-Json | Set-Content -Encoding utf8 `
    -LiteralPath (Join-Path $output "reference-sources.json")
