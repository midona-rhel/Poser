# PBI-016 — Imperative rebuild on the Crystarium foundation

| Control | Value |
|---|---|
| Status | Code phases complete on `feature/pbi-015-reactive-ui-core`; in-game checkpoints outstanding |
| Supersedes | [PBI-015](PBI-015-react-style-imgui-core.md) |
| Pixel spec | tag `reactive-final` (`bf557b7`) and the PNGs frozen from it |
| Byte baseline | `tools/ui-conformance/accepted-c71d682-hashes.txt` (71 states) |
| Approved plan | "UI Rebuild: Imperative Return on the Crystarium Foundation" |

## Why

PBI-015 delivered the accepted look and could not carry it. A 260-bone sidebar
held 30 fps, UiBuilder hitched repeatedly past 200 ms, and every migrated pane
came out larger than the imperative one it replaced (AnimationPane 703→1,410,
PoseInspectorPane 1,632→2,117, AppShellView 1,006→1,464). The framework —
solver, arena, walker, sheets, portals, 8,331 non-blank lines — is deleted. The
look it produced is kept.

## Directives

These are the standing constraints, not a phase list:

- **Copy from Brio, Character Select+ and Penumbra.** Plain per-window
  imperative `Draw()` over a shared helper layer. Brio's 19 windows average 378
  non-blank lines over one `static partial` helper class; CS+ adds window
  chassis classes; Penumbra is the performance reference.
- **Pixel parity with the accepted look.** The rebuild re-expresses; it does not
  redesign. Divergence from the frozen oracles is a defect unless the user
  accepts it as a supersession.
- **Clean slate.** Re-express rather than port framework code. Panes return to
  their imperative ancestors where an ancestor exists.
- **Penumbra-class performance.** Warm frames recompute nothing. No text is
  measured for a clipped row, ever.
- **Brio-class volume.** Panes land near their ancestor sizes; the library
  carries no framework code.
- **Concise, practical comments.** Comments state constraints the code cannot
  show. They do not narrate how the code came to be.
- **Reusability is the organizing principle** — but only where a second real
  consumer exists. A shape with one caller stays private to that caller.
- **No full archive.** The imperative Crystarium control library is the
  surviving foundation, byte-frozen by the accepted hash set. Only the reactive
  block was thrown away.

## Architecture

**Per-window imperative draw.** Each window/pane is an ordinary class whose
`Draw(origin, size)` decomposes into private `DrawXxx` methods over instance
fields, `ImRaii` scoping and plain ImGui cursor flow. View models
(`AppShellViewModel`, `ShellSidebarRow`) are the data contract and survived the
rebuild untouched — only renderers were rewritten.

**The shared helper layer** is `Poser.UI`: `Rendering\` (theme, fonts, glass,
interaction kernel, SVG), `Primitives\Tags\` (single controls) and
`Compositions\` (assemblies). It is one `static partial class Crystarium`.

**No general widget vocabulary replaced the reactive components.** Four
reactive-only *designs* became thin imperative compositions, and reactive
widenings folded into their existing imperative twins:

| Reactive design | Imperative home |
|---|---|
| Unified SearchPicker | `Poser.UI\Compositions\SearchPicker.cs` — `Crystarium.SearchPicker<T>`, a retained object; anchor from the just-reserved trigger, opaque self-shadowed panel, clipper-backed pill rows in a half-gutter `ScrollRegion`, optional icon/glyph/badge selectors and filter strips below search, captionless single / captioned multi |
| FormPair | `Poser.UI\Compositions\PageForm.cs` — `FormScope.Pair`, the paired-attribute band: row split at the middle, mirrored label+control halves |
| WindowFrame (chassis) | `Poser.UI\Compositions\WindowFrame.cs` — `Crystarium.WindowFrame`, returning `WindowFrameRects` |
| TreeRow | `Poser.UI\Primitives\Tags\TreeRow.cs` — `Crystarium.TreeRow`, absorbing the deleted `SidebarRow` |

**The Penumbra sidebar pattern** lives in `Poser\UI\Views\ShellSidebar.cs`: a
flat `List<Entry>` cache of visible rows (pre-lowercased labels, depth, guide
bitmask, action count, vertical offset), ONE dirty flag, an `ImGuiListClipper`
whose reported slots map through `_slots` to exact per-entry offsets, and
per-keystroke refiltering over the cache rather than the view model. Expansion
state is not stored there — rows carry it from the view-model builder and a
disclosure click flows out through `AppShellViewModel.OnRowExpandToggled`.

**The ink-centering system** replaces the seven per-surface tuned constants that
kept re-introducing the same defect. `TtfMetrics` reads `OS/2.sCapHeight`
(measured-`H` fallback at `ApproxCapHeightEm`); `FontRegistry.InkRise(family,
weight, sizePx)` caches the line-box-to-cap-ink delta per font key;
`Crystarium.TextInBand` is the only path that seats text in a band.

## Accepted-decision ledger

Reconstructed from every commit from tag `reactive-final` to HEAD. Each entry is
a decision the history proves was made and accepted, with the file and member
that carries it now.

### Ink centering (`aa31dc3`)

| Decision | Home |
|---|---|
| Text seats on measured font metrics, never per-surface tuned constants | `Rendering\Fonts\TtfMetrics.cs` `CapHeightEm`; `Rendering\Fonts\FontRegistry.cs` `InkRise`; `Primitives\Tags\Text.cs` `TextInBand` |
| A seat tie resolves toward the HIGHER seat — a tie resolved low is the defect this kills | `Primitives\Tags\Text.cs` `InkSnapY` |
| ONE icon-adjacent bias, −1.5 CSS px: a run beside an icon is judged against the icon ink. Two independently accepted measurements (the sidebar seat, ContextMenu's `RowInkRise`) agree on the value exactly | `Primitives\Tags\Text.cs` `IconAdjacentInkBias` |
| ContextMenu's local row-rise constant is deleted; the metric seat supersedes it | `Primitives\Tags\ContextMenu.cs` (`TextInBand(..., besideIcon: true)` call sites) |
| Eight sidebar-row states re-recorded at the accepted seat; the legacy baseline predated that acceptance | `tools\ui-conformance\accepted-c71d682-hashes.txt` |
| `arrow-left` / `arrow-right` added to the icon registry | `Icons\TablerSvgSources.cs` |

### TreeRow and WindowFrame (`2d3585f`)

| Decision | Home |
|---|---|
| ONE tree/sidebar row: guides, chevron as its own reserve, icon-adjacent label seat, 11px mono badge on the label's optical line, trailing action strip, hover/selected fills. `SidebarRow` deleted | `Primitives\Tags\TreeRow.cs` `Crystarium.TreeRow` / `TreeRowProps` |
| A trunk's FREE ends — those meeting the neighbouring row rather than this row's own arm — drop two physical px; ends terminating at the arm stay put | `Primitives\Tags\TreeRow.cs` `TreeGuideDrop` |
| Selection dominates hover: selected and selected-hover are ONE image by rule | `Primitives\Tags\TreeRow.cs` fill selection; asserted by the identical pair of accepted states |
| Pill inset: a nested pill clears its own branch arm, a root pill carries the 1px CSS inset, the 1px bottom shave stands, and the right edge is the CONTENT edge, not the window edge | `Primitives\Tags\TreeRow.cs` `TreeRootPillInset` / `TreeTrunkX` / `TreePillClearance` |
| The row's trailing inset is the caller's, so a shell action strip and a plain list row share one row | `Primitives\Tags\TreeRow.cs` `TreeRowProps.TrailingInset` |
| `ListRow` re-points onto the same row rather than keeping a second implementation | `Compositions\ScrollRegion.cs` `ScrollRegionScope.ListRow` |
| ONE window chassis: title/close bar, an optional band, an optional raised rail, body, footer band existing iff footer content is stated | `Compositions\WindowFrame.cs` `Crystarium.WindowFrame` |
| The rotated-H is the geometry: rules run WINDOW EDGE to WINDOW EDGE, past the header inset, bridged by the rail's 1px vertical rule — so the frame draws them itself instead of letting `ActionBar` rule at its narrower box | `Compositions\WindowFrame.cs` |
| Bar labels constrain only on OVERFLOW; an unconstrained title keeps its `g` descender (the twice-caught shave class) | `Compositions\WindowFrame.cs` → `ActionBarScope.Label`; `Primitives\Tags\Text.cs` `TextConstraint` |

### Picker, form pair, control upgrades (`8b7d295`)

| Decision | Home |
|---|---|
| The unified picker is ONE retained imperative object; the panel paints its own shadows rather than borrowing a surface's | `Compositions\SearchPicker.cs` `SearchPicker<T>` |
| The golden's own row metrics rule over the brief's numbers: header 40 / search 36 / pill 28 / 2px pill gap | `Compositions\SearchPicker.cs` `PickerHeaderHeight`, `PickerSearchHeight`, `PickerRowHeight`, `PickerPillVGap` |
| Picking is a decision and closes the picker; toggling is not and does not | `Compositions\SearchPicker.cs` (multi-select path) |
| The metric ink seat SUPERSEDES the reactive text constant: both picker states match their goldens in everything but a uniform −1px on text runs, measured rather than chased | `Primitives\Tags\Text.cs` `TextInBand` |
| The paired-attribute band | `Compositions\PageForm.cs` `FormScope.Pair`, `FormPairCell` |
| `DrawTextCentered` — the last line-box centering path — is deleted; the section-header title moves 1px onto the metric seat | `Compositions\PageForm.cs` `Section` |
| Segmented gains an icon variant that aligns the first tab; icon buttons gain named glyphs and `flipX` | `Primitives\Tags\SegmentedControl.cs` `IconSegmentLayout`; `Primitives\Tags\TablerIconWidgets.cs` |
| Swatches are ONE call, not a hand-assembled row | `Primitives\Tags\ColorWell.cs` `Crystarium.SwatchPalette` |
| Where the retiring renderer and the imperative baseline differ only by AA fingerprint (swatch-pill border, toggle glyph), the imperative baseline wins by principle | — (adjudication; no code) |

### Settings (`018e61e`)

| Decision | Home |
|---|---|
| The view is static and draws the shared chassis, filling only the rects the frame hands back | `Poser\UI\Views\SettingsView.cs` |
| The settings rail row is a private primitive — flush pill, full-opacity glyph. It is the settings shape, not the tree shape, and has exactly one caller | `Poser\UI\Views\SettingsView.cs` (private) |
| The rule is a divider BETWEEN sections: a page's FIRST section states `divider: false` and draws neither the rule nor the margin above it. Threaded through every `Section` overload including the standalone rail form | `Compositions\PageForm.cs` `Section(..., bool divider = true)` |
| The Swatches form row draws the accepted SwatchPalette pill | `Compositions\PageForm.cs` → `Crystarium.SwatchPalette` |
| The Segmented form row seats at the golden navigation height | `Compositions\PageForm.cs` |
| The equal-track wells row | `Compositions\PageForm.cs` `ColorWellScope` |
| `CaptureRebind` draws LAST as the raw-input boundary | `Poser\UI\Views\SettingsView.cs` |

### Shell sidebar (`9707710`)

| Decision | Home |
|---|---|
| Rebuild the flat cache only on scene revision, structural change, pitch change or a disclosure click; a keystroke refilters in place | `Poser\UI\Views\ShellSidebar.cs` `_dirty` / `_refilter` |
| The clipper is a VISIBILITY ORACLE mapped through exact per-entry offsets, not a uniform pitch: the header band and the inter-section gap survive instead of being quantized away | `Poser\UI\Views\ShellSidebar.cs` `_slots`, `Entry.Top`/`Height` |
| The cache holds (section, row) PATHS, never row references — the view model rebuilds row objects every frame, so a held reference would freeze selection and badges | `Poser\UI\Views\ShellSidebar.cs` `Entry.Section` / `Entry.Row` |
| A per-frame row-count guard catches structural changes that arrive without a revision bump | `Poser\UI\Views\ShellSidebar.cs` `_rowCounts` |
| The perf contract is p95 draw < 1.5 ms and ZERO allocation of the sidebar's own, at 300 rows over 600 warm frames | `tools\ui-conformance\verify-sidebar-perf.ps1` |

### FileDialog and the gutter rule (`73e2abf`)

| Decision | Home |
|---|---|
| Gutter-as-padding: a scroll region takes NO right padding — the reserved bar sits on the window edge and IS the right inset. A row's own trailing padding keeps its content clear of the bar while its highlight bleeds under it | `Compositions\FileDialog.cs` `DrawEntries`; the unconditional reserve is `Compositions\ScrollRegion.cs` `gutterWidth` |
| Plain back/forward arrows precede up; the nav band is closed by its own full-width rule; the quick-access rail sits on raised glass with the bridging rule | `Compositions\FileDialog.cs`; `WindowFrameProps.BandHeight` / `RailWidth` |
| Picks apply AFTER the scroll region closes — travelling refills the list mid-loop | `Compositions\FileDialog.cs` `Draw` |
| The hand-rolled double-click detector is deleted in favour of the kernel's | `Rendering\Internal\Interactive.cs` `Reserve` |
| A frame may declare that its host already painted the glass, so it does not stack a second shadow | `Compositions\WindowFrame.cs` `WindowFrameProps.HostPaintsChrome` |
| Disabled-opacity adjudication: the disabled arrow keeps the CSS-correct 0.2 that the accepted `icon-button-disabled` state froze, over the golden's double-multiplied 0.16 | `Primitives\Tags\Button.cs` `IconButtonDisabledOpacity` |
| The public dialog shape is frozen — ctor / `Open` / `Draw` / `IsOpen` plus the `Source`/`Rehome` fixture seam | `Compositions\FileDialog.cs` |

### Animation (`ed0919e`)

| Decision | Home |
|---|---|
| Every animation choice goes through the ONE shared picker; the pane supplies a CATALOG FEED (query, badge, icon, head strips) as picker options instead of owning a picker | `Poser\UI\Panes\AnimationPane.cs` `TimelineFeed`, `PickerOptionsFor`; `Compositions\SearchPicker.cs` `PickerOptions<T>` |
| A retained surface must have its options RE-STATED every frame — `Arm` snapshots them at open, so a strip's selected pill would otherwise freeze while the list filtered | `Compositions\SearchPicker.cs` `SearchPicker<T>.Update(in PickerOptions<T>)` |
| The feed memoizes per query/kind/weapon/load | `Poser\UI\Panes\AnimationPane.cs` `TimelineFeed` |
| The kind strip drops slot-impossible kinds and seeds per open; the weapon tri-filter persists across opens and applies only on Base | `Poser\UI\Panes\AnimationPane.cs` |
| Row keys carry id+kind+slot, so the aliasing fix survives as ImGui identity; the selection tick resolves through the feed's own source | `Poser\UI\Panes\AnimationPane.cs` |
| Slot memory keys by the REQUESTED slot; the pick actor is frozen; the lips catalogue builds at most once and sticks only when the catalogue answered | `Poser\UI\Panes\AnimationPane.cs` |
| STANCE is two Pair rows; GENERAL states `divider: false`; the scene menu and the picker draw AFTER the page | `Poser\UI\Panes\AnimationPane.cs` |
| A closed tab simply does not draw — the closed-portal cost dies by construction | `Poser\UI\Panes\AnimationPane.cs` (and every pane) |

### Appearance, inspector sections, pose rail (`5e7de00`)

| Decision | Home |
|---|---|
| Inspector sections are imperative `Draw(FormScope, …)` types, hosted only by the pose rail | `Poser\UI\Panes\PoseFileInspectorSection.cs`, `Poser\UI\Panes\ExpressionInspectorSection.cs` |
| Appearance keeps its full ledger: wet-surface re-reads, dispatch-time wetness, MCDF progress/import/export with every help branch, collection key by string, picker freezing, skipped-resource capping | `Poser\UI\Panes\AppearancePane.cs` |
| All three Appearance selectors go through the shared picker, drained once per frame and dispatched by owner | `Poser\UI\Panes\AppearancePane.cs` |
| The pose rail loses the reactive graft IN PLACE: rail root, prop holders and hoisted handlers deleted, each section a plain `DrawXxx` over `FormScope`; the surviving imperative majority is untouched | `Poser\UI\Panes\PoseInspectorPane.cs` |
| Gesture guards stay hoisted ABOVE the sections so a collapsed TRANSLATION cannot strand a cancelled gesture | `Poser\UI\Panes\PoseInspectorPane.cs` |
| Selector rows keep the permanent Reset slot and the help inversion; Progress rows return | `Compositions\PageForm.cs` `FormScope` |

### Shell (`9a2fd5d`)

| Decision | Home |
|---|---|
| The shell keeps its OWN chassis rather than `WindowFrame` — three titlebar cells with three different fills, no full-width title rule, a RIGHT rail and a sidebar-hosted status band — but rides the shared chrome path exactly | `Poser\UI\Views\AppShellView.cs`; `Compositions\FloatingSurface.cs` `PrependShellBlur`, `DrawChrome` |
| The main window suppresses the elevation shadow: a shadow under a chassis that IS the window reads as a halo, not as elevation. Every floating surface keeps it | `Compositions\FloatingSurface.cs` `DrawChrome(shadow:)` |
| Panel fills land over the chrome, so the asymmetric glass edge is repainted LAST | `Poser\UI\Views\AppShellView.cs` |
| Collapse-to-titlebar is an early return with no invented animation — the accepted spec has none | `Poser\UI\Views\AppShellView.cs` |
| `ShellTab.SceneTab` stays unexpressed: the accepted spec never drew its divider and no host sets it | `Poser\UI\Views\AppShellView.cs` `ShellTab.SceneTab` |
| Shell retained state is static because the view is — there is exactly one shell | `Poser\UI\Views\AppShellView.cs` |
| The view model sheds only its reactive limbs; every public field and delegate is untouched and `MainWindow` needed zero changes | `Poser\UI\Views\AppShellView.cs` `AppShellViewModel` |

### Shell layout contract (`9a2fd5d`, `73e2abf`)

| Decision | Home |
|---|---|
| ONE content origin for every tab: panes own their breathing room, the shell owns the origin, the scroll and the gutter | `Poser\UI\Views\AppShellView.cs` `DrawContentViewport` |
| Content width always excludes the gutter, so overflow can never reflow content | `Poser\UI\Views\AppShellView.cs`; `Compositions\ScrollRegion.cs` |
| The inset is measured from the CHILD, not the panel — the child is 1px narrower (the glass border pixel) and the bar hugs the child's right edge | `Poser\UI\Views\AppShellView.cs` `DrawContentViewport` |

### Deletion and rename (`c3a6fe3`)

| Decision | Home |
|---|---|
| `LegacyCrystarium` → `Crystarium`: the shim died with its directory and the 290 existing call sites resolve to the real class | repo-wide; `Poser.UI` |
| The four per-surface `OpticalTokens` text constants die — `TextInBand`'s metric seat orphaned them. `Snap()` and the rest of `Optical` survive | `Rendering\Theme.cs` `Optical` |
| The frozen golden PNGs stay as the accepted record; `i-*` are the living states | `tools\ui-conformance\golden-rebuild\` |
| Nothing merges into the accepted baseline until the in-game checkpoint, and `-AllowAdded` retires with it | `tools\ui-conformance\accepted-c71d682-hashes.txt` |

### Standing product decisions carried through unchanged

These predate the rebuild and were re-asserted by it rather than re-decided:

| Decision | Home |
|---|---|
| Form-row pitch is 34, not Picto's 30: stacked full-height controls leave property rows no separation at 30 | `Rendering\Theme.cs` `Controls.FormRowHeight` |
| No focus-visible chrome anywhere — native-styled UI, not web | `Primitives\Tags\Button.cs`, `Primitives\Tags\TextInput.cs` |
| The slider's filled span is white like the thumb; the primary blue remains only in the thumb | `Primitives\Tags\Slider.cs` |
| On the bone map, Ctrl AND Shift both extend: the map has no row order, so no range gesture reserves Shift | `Poser\UI\Panes\GraphicalBonePane.cs` |
| Bone-map selection is the theme's primary, not ImGui's style highlight | `Poser\UI\Panes\GraphicalBonePane.cs` |

## Verification

**Frozen oracles.** `tools\ui-conformance\golden-rebuild\` holds the nine
reactive-only accepted designs captured at tag `reactive-final` —
`rpicker-open`, `rpicker-multi`, `rsettings-frame`, `rsegmented`, `rswatches`,
`ricon-actions`, `rscrollarea`, `rtreerow`, `rfiledialog` — plus
`golden-rebuild-hashes.txt`. They are the pixel spec the imperative
re-expressions were judged against and are not a live gate.

**Regime.** Every phase ran:

- `verify-accepted-hashes.ps1 -AllowAdded` — 71 accepted states byte-identical.
  13 states are added and not yet accepted: the eight `i-*` re-expressions plus
  five never-accepted imperative form states (`colorwell`,
  `colorwell-disabled`, `progress`, `slider`, `slider-disabled`). They merge
  into the baseline, and `-AllowAdded` retires, on the in-game checkpoint.
- `verify-kernel.ps1 --kernel-behavior`, `verify-tokens.ps1`,
  `verify-button-clip.ps1`, `verify-actionbar-allocation.ps1`,
  `verify-batch-isolation.ps1`.
- `verify-sidebar-perf.ps1` — 300 synthetic rows, 600 warm frames, p95 under
  1.5 ms and zero sidebar-own allocation.

## Outstanding

- **In-game checkpoints.** None of the five have run. The perf acceptance is
  60 fps with a fully-expanded 260-bone actor and zero Dalamud hitch lines; the
  visual acceptance covers the metric-seat shifts (the settings-page section
  header and the picker's uniform −1px), side by side against `reactive-final`.
- **Merging the `i-*` hashes.** On acceptance, fold the 13 added states into
  `accepted-c71d682-hashes.txt` and drop `-AllowAdded` from the phase gate.
- **SVG icon painter allocation.** The painter allocates roughly 1.8 KB per icon
  draw even on mask-cache hits (16.07 MB over the sidebar perf run, all of it
  the painter). It predates this program and is a `Rendering\Svg` follow-up, not
  a sidebar cost.
- **`verify-icon-button.ps1`** fails exactly as it did before this program
  started (`plus=29`). Tracked separately; the hash gate proves the renderer is
  byte-identical.
- **`picto-reference.html` dead variants.** Cells for states the catalog no
  longer captures.
- **Pose-preview provider seam.** `FileDialog`'s preview renders nothing until a
  provider is supplied.
