# Token contract check: color parity for the six supported themes is proven
# here, never by rendering six theme catalogs. Fails on tokens.css source-hash
# drift, on any diff between a fresh regeneration and the committed
# PictoTokens.g.cs, and on any violation of the complete Theme-field mapping.
# Run after Theme.cs changes, after generate-tokens.ps1, or when the Picto
# checkout moves.

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
$committed = Join-Path $toolRoot "..\..\Poser.UI\Rendering\PictoTokens.g.cs"
if (!(Test-Path -LiteralPath $committed -PathType Leaf)) {
    throw "Committed token file not found: $committed"
}

& $exe --verify-tokens `
    (Resolve-Path -LiteralPath $tokens).Path `
    (Resolve-Path -LiteralPath $committed).Path
if ($LASTEXITCODE -ne 0) {
    throw "Token contract FAILED — see output above."
}
