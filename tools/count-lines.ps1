# Reports the historical PBI-015 line-count scope. Production includes Poser.UI,
# Poser/UI, and root plugin C# files. Tooling includes selected source formats
# under tools/. Generated files, Markdown, and build/output directories are excluded.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$generated = @("TablerSvgSources.cs", "ColorTokens.g.cs")
$outputDirs = '\\(bin|obj|artifacts|out|node_modules|__pycache__)\\'

function Count($paths, $filter) {
    $files = Get-ChildItem -Path $paths -Recurse -Include $filter -File |
        Where-Object {
            $_.FullName -notmatch $outputDirs -and
            $generated -notcontains $_.Name -and
            $_.Name -notmatch '\.generated\.'
        }
    ($files | Get-Content | Measure-Object -Line).Lines
}

# Measure-Object -Line excludes blank lines, so these totals are lower than
# tools such as wc -l. Keep that behavior when comparing historical counts.
$pluginRoot = (Get-ChildItem "$root\Poser\*.cs" -File |
    Where-Object { $generated -notcontains $_.Name } |
    Get-Content | Measure-Object -Line).Lines
$production = (Count @("$root\Poser.UI", "$root\Poser\UI") "*.cs") + $pluginRoot
$tooling = Count @("$root\tools") @("*.cs", "*.py", "*.ps1", "*.html", "*.js", "*.json")
"production handwritten: $production"
"tooling source:         $tooling"
