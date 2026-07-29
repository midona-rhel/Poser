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
catalog. The default is the complete six-theme, three-scale matrix. Narrow it
with `-Scales` and `-Themes`; use `-Clean` when beginning a new regression set.
Without `-Clean`, new captures replace their matching entries and leave other
components visible in the same catalog. `-OpenReport` opens that scrollable
catalog in its own window.
The combobox reference is Picto Settings' exact `Sort by / Date Added`
`CmSelect`: its seven real options, intrinsic width, and open-menu rules.
Add `-Themes dark,light,lightgray,gray,blue,purple` to compare Picto's
deterministic color themes. Auto is resolved by Poser at runtime;
platform-material themes are deliberately unsupported.

The generated `artifacts/index.html` links each result. Every result contains:

- the Picto reference raster;
- the current Crystarium raster;
- an exact red pixel-failure map with bounded mismatch regions;
- measured foreground bounds, alignment, missing/extra coverage, color delta,
  and bright/text-ink vertical offset;
- the Picto source-manifest hash, candidate assembly hash, commit, and dirty
  state. Preserved results from another source or assembly are marked stale.

Exact equality is the pass gate. The measurements explain likely causes but do
not waive antialiasing differences. Generated captures and reports are ignored
by Git.
