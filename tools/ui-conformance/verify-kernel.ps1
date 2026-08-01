# Interaction-kernel invariants — the guarantees the pixel fixtures cannot
# reach, driven by real ImGui input frames in the capture host:
# - a control under a higher surface takes neither pointer nor keyboard
#   activation, and never opens a drag;
# - drag OWNERSHIP, not the current occlusion state, pairs DragBegan with
#   DragEnded, so a surface opening over a held control still releases
#   exactly once;
# - Motion throws on a dropped, added, reordered or repeated channel, and
#   a zero-duration transition arrives on the call that retargets it;
# - clearing a text input empties it AND hands focus back to the field.

$ErrorActionPreference = "Stop"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $toolRoot `
    "Crystarium.Capture\bin\Debug\net10.0-windows\Crystarium.Capture.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the capture host first."
}

& $exe --kernel-behavior
if ($LASTEXITCODE -ne 0) {
    throw "Interaction-kernel invariants failed."
}
