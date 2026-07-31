# Token equality: color parity for the six supported themes is proven by
# comparing Crystarium's Theme values against an independent parse of the
# sibling Picto tokens.css — never by rendering six theme catalogs. Run this
# when Theme.cs / PictoTokens.cs change or the Picto checkout moves.

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

& $exe --verify-tokens (Resolve-Path -LiteralPath $tokens).Path
if ($LASTEXITCODE -ne 0) {
    throw "Token equality FAILED — Theme colors diverge from tokens.css."
}
