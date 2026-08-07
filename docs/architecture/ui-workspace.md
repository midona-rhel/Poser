# UI workspace

## Surfaces

Retained surfaces: main window, settings, spawn browser, skeleton overlay,
gizmo overlay (`UiWindowSet`, exactly five). `GraphicalBonePane` is
main-window content.

- Main surface + gizmo canvas open/close with GPose; the skeleton overlay
  starts Off each session (titlebar toggle; Alt temporarily hides dots).
- UI owns filter text, disclosure, mode, hover, widget state, formatting. It
  never owns another selection, native baselines, pose accumulation, undo
  state, or a cached entity as identity — rows carry stable ids and spatial
  reads go through the viewport projection per frame.
- Sizing: 48 px titlebar; 220–400 px sidebar; 280 px inspector rail that
  adds/removes exactly 280 px; minimums 1110 px with rail, 830 without.
  Collapse is a real 48 px titlebar-only window. Use Dalamud `Window.Size`
  in `PreDraw`, never `ImGui.SetWindowSize`.
- Scene tree: click selects, Ctrl toggles, Shift range-selects visible order,
  category rows navigate only. Everything seeds **collapsed**; only explicit
  disclosure clicks change tree state — external selection never expands.
  One row per actor: Character categories appear directly (no wrapper row);
  each present auxiliary slot adds one collapsed group (Main Hand, Off
  Hand, Prop, Ornament) showing its real bone hierarchy, absent when the
  skeleton is absent.
- Pose surface: fixed mode header (Body/Face/Matrix/3D left; Mirror
  selection, Physics, Animation right), middle viewport, fixed footer.
  Matrix is the only scrolling document; Matrix and 3D operate on the
  primary bone's slot skeleton. Body/Face maps are Character-only and never
  highlight a same-named auxiliary bone; the skeleton overlay projects
  every present slot with that slot's own model matrix. Mirror selection
  (Brio `GraphicalSidesSwapped`) swaps sided dots on the maps only.

## Shape of the code

Every window and pane is an ordinary class whose `Draw(origin, size)`
decomposes into private `DrawXxx` methods over instance fields and plain ImGui
cursor flow. There is no framework between a pane and ImGui — no retained
layout tree, no arena, no solver, no component scopes. What is shared is a
helper library, `Poser.UI`, exposed as one `static partial class Crystarium`
and layered in three directories:

**`Rendering\`** — everything a control needs but never reimplements. `Theme`
and `PictoTokens.g.cs` (design tokens); `Fonts\` (`FontRegistry`, `TtfMetrics`,
`WindowsFontFallback`); `Effects\GlassChrome` (blur/glass); `Internal\`
(`Interactive` — the input kernel — and `Motion`); `Svg\` (the runtime SVG
document cache, path parser, stroke mask, tessellator and renderer);
`BoxRenderer` / `ControlPaint` / `ColorEx` / `Transition`.

**`Primitives\Tags\`** — single controls: `Button`, `Text`, `TreeRow`,
`Dropdown`, `ColorWell`, `HoverHelp`, `ContextMenu`, `SegmentedControl`,
`TextInput`, `AxisWell`, `Modal`, `Popover`, `Slider`, `Switch`, `Checkbox`,
`ProgressBar`, `FilterPill`, `TablerIconWidgets`, `Misc` (image).
`Primitives\ControlStyle.cs` holds the typed width/height semantics and
`ControlSizing.Resolve`.

**`Compositions\`** — assemblies of primitives: `ActionBar`, `FloatingSurface`,
`PageForm`, `ScrollRegion`, `SearchPicker`, `FileDialog`, `WindowFrame`.

Reusability follows real consumers. A shape with exactly one caller stays
private to that caller — the settings rail row is a private primitive of
`SettingsView`, not a library control, because its flush pill and full-opacity
glyph are the settings shape and nothing else wants them.

## The window frame

`Crystarium.WindowFrame` is the ONE window chassis, and every floating Poser
window is that frame told different slots: glass chrome, a title bar with its
close affordance, an optional band under it, an optional left rail, the body,
and a footer band that exists iff footer content is stated. It returns
`WindowFrameRects`; the caller fills the rail and body rectangles and owns any
scrolling and any content inset inside them.

The rotated-H is the geometry: full-width rules under the title bar and over
the footer, bridged by the rail's 1 px vertical rule. The rules run WINDOW EDGE
to WINDOW EDGE, past the inset the bars pad their items to, so the frame draws
them itself rather than letting `ActionBar` rule at its narrower box.

`HostPaintsChrome` exists because a host may already have painted the glass
(`FloatingSurface.Window` does for every window it hosts) and the frame must
not stack a second shadow over the first.

The main shell does NOT use `WindowFrame`: its titlebar is three cells with
three different fills, it has no full-width title rule, its rail is on the
RIGHT and its status band lives in the sidebar. It rides the same chrome path —
`FloatingSurface.PrependShellBlur`, then `DrawChrome` with the elevation shadow
suppressed, then panel fills, then the glass border repainted last — but owns
its own frame.

## Shell layout contract

`AppShellView` owns the scroll, the gutter and the content origin; panes draw
into what it hands them and never rebuild any of it locally.

- ONE content origin for every tab. Panes own their internal breathing room;
  the shell owns where content starts.
- Content width always excludes the scrollbar gutter, so overflow can never
  reflow content.
- The inset is measured from the viewport CHILD, not the panel: the child is
  1 px narrower than the panel (the glass border pixel) and the bar hugs the
  child's right edge.
- A pane declares which of the three hostings it wants:
  `ContentOwnsViewport` (the pane scrolls internally and the shell viewport
  stays fixed — Pose uses this for its fixed mode tabs and footer),
  `ContentUsesPage` (the pane's root is `Crystarium.Page`, which already owns
  the inset and extent bookkeeping), or neither (the shell applies the
  horizontal inset itself).

The gutter rule is the general form of the same contract: every scrollable view
reserves the scrollbar gutter UNCONDITIONALLY, and the reserved bar space IS the
right inset. `ScrollRegion` never gives content the gutter width back, so the
bar appearing or disappearing never reflows anything; the region takes no right
padding of its own, and a row's own trailing padding is what keeps its content
clear of the bar while its highlight bleeds under it. A floating surface may
state a narrower bar than the shell's (the picker's is half).

## The sidebar

`Poser\UI\Views\ShellSidebar.cs` is the Penumbra pattern: the search field,
section headers and tree drawn over a FLAT cache of visible entries.

- The cache (`_source`, filtered into `_entries`) holds everything derived once
  — the id string, the pre-lowercased label the filter reads, the guide bitmask,
  the vertical offset — so no frame recomputes it.
- ONE dirty flag. It is set by the scene revision, a structural change, a pitch
  change, or a disclosure click. A keystroke is the one input that does not
  reach it: the filter refilters the cache in place.
- The `ImGuiListClipper` is a visibility ORACLE, not a layout: `_slots` maps the
  slot band it reports to exact per-entry offsets, so the header band and the
  inter-section gap survive instead of being quantized to a uniform pitch.
- The cache holds (section, row) PATHS, never row references. The view model
  rebuilds row objects every frame, so a held reference would freeze selection
  and badges; anything that can change without a structural change is read from
  the live row through the cached path. `_rowCounts` catches a structural change
  that arrives without a revision bump.
- A warm frame walks no tree, builds no string, and measures no text for a
  clipped row. `verify-sidebar-perf.ps1` is the gate: 300 rows, 600 warm frames,
  p95 draw under 1.5 ms and zero allocation of the sidebar's own.

**Expansion state is not stored in the sidebar.** Rows carry `Expanded` and
`ExpandKey` from the view-model builder, whose truth is `MainWindow`'s collapsed
set; a disclosure click flows out through `AppShellViewModel.OnRowExpandToggled`
and only marks the cache dirty, so the next frame re-splices from rebuilt rows.

## Text and ink centering

`Crystarium.Text` / `TextAt` / `MeasureText` / `TruncateText` with a `TextStyle`
(token size, weight, family, theme color, opacity-based disabled) is the ONE
text renderer. Input is NFC-normalized for presentation only, and presentation
normalization also canonicalizes CRLF and lone CR to LF as the HTML parser does.
Measurement and rendering must share one resolved style value.

Width behavior is a typed `TextConstraint`: `Intrinsic` carries no width,
`Truncate(width)` requires one, `Wrap(width, lineHeight?, whitespace?)` owns its
optional CSS line-height and `TextWhitespace` policy (Normal / PreLine /
PreWrap); non-positive dimensions are rejected. A constrained inline run
occupies its constraint width in layout so siblings flow from the box edge, and
carries a typed `TextAlign` (Start default; End pins the run's end to the box
edge). Truncation backs off whole grapheme clusters and the renderer CLIPS to
the box like `overflow: hidden` — when even the ellipsis cannot fit, the
ORIGINAL run draws through the clip, exactly Blink's narrow behavior; string
fitting is composition-internal and never a substitute for that clip. Wrapping
never hard-breaks an over-wide word, accumulates the fractional line advance
unrounded, half-leading-centers each line, and expands preserved tabs to 8-space
stops under PreWrap. CJK (Default family only — mono and italic stay lean)
merges the face Chromium's Segoe UI font-link chain falls back to (Meiryo UI
before Yu Gothic UI), resolved by `WindowsFontFallback` shared verbatim between
the game and the capture host.

**A constraint applies only on OVERFLOW.** A run that fits is drawn whole; a bar
label that fits keeps its descenders. Shaving a fitting run is a recurring
defect class, not a style.

**`Crystarium.TextInBand` is the ONLY way to seat text in a band, and no
surface may carry its own centering constant.** Line-box centering alone reads
low because internal leading is asymmetric, so `TextInBand` seats the INK — the
cap-to-baseline band — on the band's midline, using the face's real metrics:
`TtfMetrics` reads `OS/2.sCapHeight` and `FontRegistry.InkRise(family, weight,
sizePx)` caches the line-box-to-cap-ink delta per font key. A seat tie breaks
toward the HIGHER seat, because a tie resolved low reproduces the exact defect
this exists to kill. There is ONE variation: `besideIcon` adds
`IconAdjacentInkBias` (−1.5 CSS px), because the eye judges a run beside an icon
against the ICON's ink centroid rather than the band centre. `TextInBand` snaps
Y itself so the tie policy applies; `Optical.Snap` still owns X and is unchanged
globally.

## Icons

`TablerSvgSources.cs` is generated — never hand-edit; `PoserIconSources` wins.
Mirrored pairs reuse one glyph with `flipX` (undo/redo). `Crystarium.Icon`
(inline, LOGICAL CSS-pixel size — the same semantics as text; scaling happens
once inside the renderer) and `Crystarium.IconIn` (screen-space box for composed
controls) are the ONE icon geometry path: min-side square fit, centering,
whole-pixel snap, tint composition (theme text × opacity × disabled opacity),
the optional stroke-width override (Tabler React `stroke` prop), and SVG round
caps/joins honored by the stroke renderer. Composed controls route through it;
no control carries its own fit/center/tint recipe.

Fonts: CSS-size conversion lives in `FontRegistry` — sizes are CSS-pixel
semantics scaled per font file; there is NO glyph offset and no per-widget font
padding: with that sizing ImGui's baseline already matches the browser line box,
and any nudge would shift every text run.

## UiKernel

`Interactive.Reserve` is the ONE control hit-test — hover, press/release,
keyboard activation, and the pointer events (click, double-click, drag
begin/end/delta), ALL occlusion-gated: pointer events by pointer occlusion,
Enter/Space by keyboard OWNERSHIP first (while the exclusive chain is open its
TOPMOST link owns the keyboard globally: only owners on that link can be
activated, whether or not the surface covers the control, so an ancestor surface
cannot be Entered from behind its child and regains the keyboard when the child
releases; the claim frame counts before any rectangle exists) and by rect
occlusion for ordinary overlapping surfaces, and drags by accepted ownership (a
drag begun un-occluded ends exactly once; a swallowed press never emits an end).

The ONLY remaining direct ImGui input queries are these named exceptions, and no
new widget-local input handling may join them: native-widget wrappers
(`TextInput`'s InputText focus/hover), popup lifecycle (`FloatingMenu`
dismissal, `HoverHelp`'s geometric help hover — the ONE help surface),
`AxisWell`'s deferred inline-edit block, and `Slider`'s pointer-position value
math.

`Motion` is the ONE animation store — one group record per identity (channel set
fixed per id, enforced; zero-duration snaps), a constant-rate ramp mode, one
prune policy; BOTH modes reseed rather than advance when the stored frame is not
strictly behind the current one (same-frame duplicate, or a recreated context
whose counter restarted); components own no transition dictionaries.

`ControlSizing.Resolve` is the ONE style→logical→scaled resolution preamble.
Popovers open only through `Crystarium.OpenPopover` (the lower-level primitive
is internal); popups claim/keep/release the `Interactive` exclusive chain only
through `FloatingSurface`'s open/sync/release helpers, and all floating
placement (anchored, point, side-preference) lives in `FloatingSurface`. The
disabled-help hover gate is `HoverHelp.Gate`.

These invariants are proven by `verify-kernel.ps1` (`--kernel-behavior`), run
when kernel code changes.

## Theme and tokens

The active `Theme` value owns colors, typography, metrics, radii, shadows,
motion, and optical corrections together; a theme change installs one complete
replacement value rather than mutating tokens. The CANONICAL color source is the
sibling Picto `tokens.css`; `PictoTokens.g.cs` is committed GENERATED output
(regenerate with `generate-tokens.ps1` — developer-only; production
build/load/packaging consume the committed file and never need Picto or a
generator). Only tokens Crystarium consumes are generated. Theme factories and
the two family builders wire every token-derived field to it; metrics,
typography, radii, and motion stay typed handwritten members.

Six-theme color parity is proven by `verify-tokens.ps1` — source-hash drift,
regeneration diff, and the COMPLETE field mapping (top-level, Chrome, Glass,
Palette.Primary) with intentional differences classified once as extensions —
never by rendering six themes. The persisted selector mirrors Picto's portable
color themes; Auto resolves the Windows app mode. Platform window-material
themes are out of scope.

Crystarium is the only product-facing API. Pages supply current state,
callbacks, and typed `ControlStyle` width/height semantics through `Page`,
`ActionBar`, `Section`, `Form`/`FormRow` and `ScrollRegion`; compositions
resolve `Fill` against their allocated region. `FloatingSurface` alone owns
floating placement and glass fill, blur, border and shadow.

## Controls with non-obvious contracts

**Text buttons.** `Crystarium.Button` is the Picto action-button family
(`actionButton.module.css`) with a typed `ButtonVariant` — Secondary (`.btn`),
Primary (`.btnPrimary`), Danger (`.btnDanger`, CSS-literal colors) — never
boolean presentation flags; `ControlStyle`'s Bare/Selected/Slashed are
icon/toggle-only. Geometry: 32 px default height, 16 px horizontal padding, 6 px
radius, 1 px border, label centered through the canonical text path and CLIPPED
to the visual bounds; measurement, drawing, hit testing, and layout reservation
resolve from the same rectangle. The background follows Picto's 150 ms ease
hover transition through the `Motion` store keyed by stable ImGui identity;
borders and text switch instantly like the CSS. Activation is release-inside
(drag-out cancels); Enter/Space activate through the kernel's keyboard-ownership
rules; disabled buttons cannot activate, take no hover styling, and keep their
`HoverHelp` explanation.

`.btn:disabled` is CSS GROUP opacity reproduced through the ONE existing drawing
path: non-overlapping chrome (fill inset to the border's inner edge, the ring
carrying the analytically flattened border-over-fill color) plus the canonical
`TextAt` label with compensated color/alpha — exact for every backdrop when the
fill is translucent, and surface-referenced (exact over the theme surface) for
an opaque fill, since affine over-blending cannot express a group over an
unknown backdrop. There is no second rasterizer or texture path. Content width
is CSS border-box. Compositions forward allocated widths into the same component
(`ButtonAtWidth`); `ActionBar` measures its own items and resolves Fill against
only its remaining allocation, never ambient window availability.

NO component draws focus-visible chrome — PRODUCT DECISION: native-styled UI,
not web; Picto's `:focus-visible` outlines are deliberately not reproduced
anywhere.

**Tree rows.** `Crystarium.TreeRow` is the ONE sidebar/tree row — a 26 px band
carrying the highlight pill, the connector guides, the disclosure, the mark, the
label and a right-aligned badge. Everything the guides need is `Depth`,
`Trunks` and `IsLastChild`; the branch shape is DERIVED, never stated.

Two targets, ONE outcome: it returns `TreeRowAction`
(`None`/`Selected`/`Expander`/`Context`), not a flag per target, because picto's
`.expandArrow` `onClick` stops propagation and double activation must be
unrepresentable. The row and its disclosure are two REAL reserves: the row is
submitted first and yields arbitration through `SetItemAllowOverlap`, so a press
landing on the chevron — or on a caller's action — takes ImGui's active id away
from it and the outcomes are mutually exclusive by construction rather than by a
mouse-x comparison. Activation is release-inside on whichever item OWNS the
press; a row with no expander reserves no arrow so its whole width selects. Row
hover stays the PARENT's (the arrow is a child, so pointing at it keeps the pill
and the full-opacity icon), while `.expandArrow:hover .triangle` — opacity
.7 → 1 over `--duration-normal` — is scoped to the arrow box alone and rides its
own Motion identity, since the highlight transitions at `--duration-fast` and
one group shares one clock. Selection DOMINATES hover: a selected row and a
selected-hovered row are one image by rule.

ACTIONS ARE THE CALLER'S: state `ActionSlots` and the row reserves the strip's
span (the label truncates against it), then reports the screen-space top-left of
the first square. The caller seats its own controls there; nothing about their
appearance is the row's business. `ScrollRegionScope.ListRow` is this same row,
not a second implementation.

**Hover help.** `Crystarium.HoverHelp` is the ONE explanatory surface (picto
KbdTooltip: 400 ms open, instant exit start, the 150 ms Mantine pop entering and
exiting as one composited surface, glass card on the foreground draw list, no
input, no layout impact). Controls register only stable id + target rect + text
(+ shortcut, side); `Preview` covers truncation without the delay; the last
registration of a frame wins, so a semantic row outranks its own wells. No
native ImGui tooltip may coexist for a migrated target.

**The picker.** `Crystarium.SearchPicker<T>` is the ONE search-and-choose
surface: an opaque panel painting its own shadows, anchored from the trigger
just reserved, with clipper-backed pill rows in a half-gutter `ScrollRegion`.
Everything beyond the plain name list is optional and defaulted off
(`PickerOptions<T>`): a caller-supplied `Query` REPLACES the built-in name
contains (a catalog that matches ids and narrows by kind is not a predicate over
a label), plus texture/glyph marks, a mono badge and up to two filter strips
below the search field. Picking is a decision and closes the picker; toggling in
multi-select is not and does not. It is a RETAINED object, so its options must
be re-stated every frame via `Update` — `Arm` snapshots them at open, and a
stale snapshot freezes a strip's selected pill while the list filters.

**Forms.** `Crystarium.Page` / `Section` / `FormScope` own the shared
label/control/value columns and one semantic density per row. The section rule
is a divider BETWEEN sections: a page's FIRST section states `divider: false`
and draws neither the rule nor the margin above it. `FormScope.Pair` is the
paired-attribute band (row split at the middle, mirrored label+control halves);
`ColorWellScope` is the equal-track wells row.

## Conformance harness

`tools\ui-conformance\` compares Crystarium against the Picto reference and
against its own accepted bytes. Two independent things live there:

**The comparison sheets** (`run.ps1`, `sheets.py`, `picto-reference.html`,
`Crystarium.Capture`, `sheet-catalog.json`). One headless Edge process renders
every reference state cell on a single page, each cell in its own shadow root;
one `Crystarium.Capture --batch` process renders each candidate state in a fresh
ImGui context with REAL pointer, keyboard and frame timing — states are never
visually forced. `sheets.py --compose` pairs them into Picto | Crystarium | red
diff with an overlay slider. Judge the sheets visually; the percentages exist to
localize, not as a pass gate.

**The byte gates**, which ARE pass/fail:

- `verify-accepted-hashes.ps1` — every candidate state against
  `accepted-c71d682-hashes.txt`. Added / missing / changed all fail; the
  accepted file grows only on user acceptance, and `-AllowAdded` is the
  development affordance for states awaiting it.
- `verify-kernel.ps1 --kernel-behavior` — the interaction invariants above.
- `verify-tokens.ps1` — six-theme color parity by token equality.
- `verify-sidebar-perf.ps1` — the sidebar's p95 draw and zero-allocation gates.
- `verify-button-clip.ps1`, `verify-icon-button.ps1`,
  `verify-actionbar-allocation.ps1`, `verify-batch-isolation.ps1` —
  engine-level behavioral invariants.

`golden-rebuild\` holds the nine PNGs frozen at tag `reactive-final`: the
accepted look of the designs that existed only in the deleted reactive layer.
They are the record the imperative re-expressions were judged against, not a
live gate. See [PBI-016](../backlog/PBI-016-imperative-rebuild.md).
