# Developer-only regeneration of the committed PictoTokens.g.cs from the
# CANONICAL sibling Picto tokens.css. Production build/load/packaging never
# run this — they consume the committed output. Run it when tokens.css
# changes, then rebuild and run verify-tokens.ps1.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}
$tokens = Join-Path $toolRoot "..\..\..\Picto\src\shared\styles\tokens.css"
if (!(Test-Path -LiteralPath $tokens -PathType Leaf)) {
    throw "Sibling Picto checkout not found: $tokens"
}
$output = Join-Path $toolRoot "..\..\Poser.UI\Rendering\PictoTokens.g.cs"

& $exe --generate-tokens `
    (Resolve-Path -LiteralPath $tokens).Path `
    (Resolve-Path -LiteralPath $output).Path
if ($LASTEXITCODE -ne 0) {
    throw "Token generation failed."
}
