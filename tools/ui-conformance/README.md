# UI conformance — component sheets

One labeled sheet per component, containing every accepted visual state:
Picto reference, Crystarium candidate, red diff, and an overlay slider in one
comparison window. This is a fast visual inspection tool, not a report
generator — there are no per-state report pages.

Run the default catalog (dark @ 100%, all components):

```powershell
.\tools\ui-conformance\run.ps1
```

`run.ps1 text-buttons` limits candidate capture and composition to one sheet.
`-OpenReport` opens the window. Warm default runs complete in seconds; the
phase gate is ≤30 s target / ≤60 s hard, excluding compilation.

How the two sides produce the whole catalog:

- **Picto side — ONE Edge process.** `picto-reference.html` renders every
  state cell on a single page; each cell is its own shadow root loading
  exactly the CSS modules that state needs (the same per-state isolation the
  old one-page-per-state flow had, since CSS-module class names collide
  across modules). `sheets.py --layout` positions the cells — origins snap
  to multiples of 8 so 1.25×/1.5× device scales stay on integer pixels — and
  one headless screenshot captures the page.
- **Crystarium side — ONE capture process.** `Crystarium.Capture --batch`
  renders each state in a fresh ImGui context with REAL pointer, keyboard,
  and frame timing (hover transitions advance actual frames; pressed states
  hold an actual primary button). States are never visually forced.
- **Composition.** `sheets.py --compose` slices the catalog screenshot,
  pairs each cell with its candidate capture, and stamps both sheets with
  identical chrome and labels (diff-silent), then writes the red diff and
  rebuilds `artifacts/index.html` — component list, combo selector, three
  synchronized columns (Picto | Crystarium | Red diff), zoom, and hidden
  diagnostics (per-cell exact and significant percentages, max channel
  delta, provenance) in a collapsed Diagnostics view. The visible diff and
  the summary badges use SIGNIFICANT differences (max channel delta > 8);
  exact counts stay diagnostics. Combos whose provenance does not match the
  current run are hidden; partial runs merge into a combo only when its
  provenance matches exactly (`sheets.py --verify-merge` self-checks this).

`sheet-catalog.json` is the single source for components, states, labels,
and cell sizes; `run.ps1` asserts the candidate catalog (`--list`) and the
generated icon fixtures agree with it, so neither side can drift silently.

Diagnostics that are NOT part of the default run:

- Scale sweeps (`-Scales 1,1.25,1.5`) — run when geometry code changes.
- Non-dark themes (`-Themes light` …) — run when compositing code changes.
  Six-theme COLOR parity is proven by `verify-tokens.ps1` (token equality),
  never by rendered theme runs.
- `verify-batch-isolation.ps1` — batch-vs-isolated capture equality.
- `verify-button-clip.ps1`, `verify-icon-button.ps1`,
  `verify-actionbar-allocation.ps1` — engine-level behavioral invariants
  (release-inside, drag-out cancellation, clip, hover reconciliation).

Provenance: the reference manifest hashes the browser executable, every
reference source (including the sibling Picto CSS), and the resolved fonts;
the candidate manifest hashes the rendering binaries and fonts. Both hashes
and the candidate commit are recorded in each combo and shown in the
window's Diagnostics. References are reused warm while the identity is
unchanged; `-Clean` starts a fresh artifact tree.

References render with `--disable-lcd-text`: the candidate's ImGui atlas is
single-channel alpha, in game as in capture, so ClearType subpixel fringe is
un-matchable noise. Remaining mismatch percentages are dominated by real
rasterizer divergence (DirectWrite grid-fits outlines; the stb pipeline is
unhinted) and browser blend rounding — judge the sheets visually; the
numbers exist to localize and compare, not as a pass gate.
