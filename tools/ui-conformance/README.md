# UI conformance

This is a deterministic Picto-to-Crystarium pixel comparison, not a second UI
preview. Picto references load the actual component CSS from the sibling
`Picto` checkout. Crystarium candidates render the current `Poser.UI` assembly
through a focused hidden D3D11 host.

Run a named comparison:

```powershell
.\tools\ui-conformance\run.ps1 combobox
```

`combobox` expands to the closed and open dropdown states. Other groups are
`text`, `button`, `icon-button`, `switch`, `input`, and `sidebar`; `all` runs the full
catalog. Split the axes by what they detect: geometry is theme-invariant
(Picto themes change color tokens only), so run scales against one theme
(`-Scales 1,1.25,1.5 -Themes dark`). Color parity across the six supported
themes is proven by TOKEN EQUALITY, not rendering: `verify-tokens.ps1`
(`Crystarium.Capture --verify-tokens`) parses the sibling Picto `tokens.css`
independently of `PictoTokens.cs` and asserts every token-derived `Theme`
color equals the CSS-resolved value per theme cascade, listing Poser-only
extension colors explicitly. Rendered non-dark themes remain available
on demand (`-Themes light` …) as a compositing diagnostic, never as the
color-parity gate. Candidate captures batch PER COMPONENT — one host
process per component keeps the boot/D3D/atlas win with no cross-component
state leakage; `verify-batch-isolation.ps1` demonstrates hash equality
against fully isolated captures. Reference captures run six-wide in
parallel, so each run stays in minutes. Use `-Clean` when beginning a new
regression set.
Without `-Clean`, new captures replace their matching entries and leave other
components visible in the same catalog. `-OpenReport` opens that scrollable
catalog in its own window.
The combobox reference is Picto Settings' exact `Sort by / Date Added`
`CmSelect`: its seven real options, intrinsic width, and open-menu rules.
Auto is resolved by Poser at runtime; platform-material themes are
deliberately unsupported.

The generated `artifacts/index.html` links each result. Every result contains:

- the Picto reference raster;
- the current Crystarium raster;
- an exact red pixel-failure map with bounded mismatch regions;
- measured foreground bounds, alignment, missing/extra coverage, color delta,
  and bright/text-ink vertical offset;
- the Picto source-manifest hash, the candidate-manifest hash (an ordered
  SHA-256 manifest over the rendering binaries — `Poser.UI.dll`,
  `Crystarium.Capture.dll`, the managed ImGui bindings, native
  `cimgui.dll` — plus every resolvable candidate font file, written to
  `artifacts/crystarium/candidate-manifest.json`), commit, and dirty
  state. The reference manifest likewise records the browser executable
  hash and the reference-side font identities. Preserved results from
  another source, build, or rendering environment are marked stale.

References render with `--disable-lcd-text`: the candidate's ImGui atlas is
single-channel alpha (greyscale), in game as in capture, so ClearType subpixel
fringe on the reference is un-matchable noise, not signal. What remains in the
diffs is real rasterizer divergence — DirectWrite grid-fits outlines while the
stb pipeline (the same one Dalamud renders with in game) is unhinted.

Exact equality is the pass gate. The measurements explain likely causes but do
not waive antialiasing differences. Generated captures and reports are ignored
by Git.
