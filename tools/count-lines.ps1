# PBI-015 accounting: the ONE reproducible line count. Reported per slice.
# Handwritten production = Poser.UI + Poser/UI + the plugin root, excluding
# generated files. Tooling source = every handwritten source file under
# tools/ (.cs .py .ps1 .html .js .json; Markdown documentation is
# intentionally excluded). Generated files and output/artifact directories
# are never counted as handwritten lines.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$generated = @("TablerSvgSources.cs", "PictoTokens.g.cs")
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

# Scope matches PBI-015's stated baseline measure: the UI production
# surface (widget library + product UI + plugin root wiring). Note:
# Measure-Object -Line skips blank lines, so figures run lower than
# wc -l; the committed baseline below is THIS script's semantics.
$pluginRoot = (Get-ChildItem "$root\Poser\*.cs" -File |
    Where-Object { $generated -notcontains $_.Name } |
    Get-Content | Measure-Object -Line).Lines
$production = (Count @("$root\Poser.UI", "$root\Poser\UI") "*.cs") + $pluginRoot
$tooling = Count @("$root\tools") @("*.cs", "*.py", "*.ps1", "*.html", "*.js", "*.json")
"production handwritten: $production"
"tooling source:         $tooling"
