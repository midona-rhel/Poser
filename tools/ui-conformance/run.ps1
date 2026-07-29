param(
    [Parameter(Position = 0)]
    [string]$Component = "all",
    [double[]]$Scales = @(1.0),
    [string[]]$Themes = @("dark"),
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
if (Test-Path -LiteralPath $resultsFull) {
    Remove-Item -LiteralPath $resultsFull -Recurse -Force
}

$aliases = @{
    "button" = @("action-button", "primary-button")
    "icon-button" = @("icon-button", "icon-button-active")
    "switch" = @("switch-off", "switch-on")
    "input" = @("text-input", "search-input")
    "combobox" = @("dropdown-closed", "dropdown-open")
    "dropdown" = @("dropdown-closed", "dropdown-open")
    "sidebar" = @("sidebar-row", "sidebar-row-selected")
}
$all = @(
    "action-button", "primary-button",
    "icon-button", "icon-button-active",
    "switch-off", "switch-on",
    "text-input", "search-input",
    "dropdown-closed", "dropdown-open",
    "color-palette",
    "sidebar-row", "sidebar-row-selected",
    "property-row", "section",
    "tooltip", "context-menu", "modal"
)
$components = if ($Component -eq "all") {
    $all
} elseif ($aliases.ContainsKey($Component)) {
    $aliases[$Component]
} elseif ($all -contains $Component) {
    @($Component)
} else {
    throw "Unknown component '$Component'. Use all, combobox, button, switch, input, sidebar, or a catalog name."
}

dotnet build $project -c Debug --no-restore -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "Capture host build failed." }

& (Join-Path $toolRoot "capture-picto-references.ps1") `
    -Components $components `
    -Scales $Scales `
    -Themes $Themes

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
                --scale $scale
            if ($LASTEXITCODE -ne 0) {
                throw "Comparison failed for $name, $theme at $scale."
            }
        }
    }
}

python (Join-Path $toolRoot "compare.py") --aggregate $artifacts
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
