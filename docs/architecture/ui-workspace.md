# UI workspace

Retained surfaces: main window, settings, skeleton overlay, gizmo overlay
(`UiWindowSet`, exactly four). `GraphicalBonePane` is main-window content.

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
- Icons: `TablerSvgSources.cs` is generated — never hand-edit;
  `PoserIconSources` wins. Mirrored pairs reuse one glyph with `flipX`
  (undo/redo). `Crystarium.Icon` (inline, LOGICAL CSS-pixel size — the
  same semantics as text; scaling happens once inside the renderer) and
  `Crystarium.IconIn` (screen-space box for composed controls) are the
  ONE icon geometry path: min-side square fit, centering, whole-pixel
  snap, tint composition (theme text × opacity × disabled opacity), the
  optional stroke-width override (Tabler React `stroke` prop), and SVG
  round caps/joins honored by the stroke renderer. Composed controls and
  `BoxStyle.BackgroundSvg` route through it; no control carries its own
  fit/center/tint recipe. Fonts: CSS-size conversion lives in `FontRegistry` — sizes
  are CSS-pixel semantics scaled per font file; there is NO glyph offset
  and no per-widget font padding: with that sizing ImGui's baseline
  already matches the browser line box, and any nudge would shift every
  text run.
- Text: `Crystarium.Text`/`TextAt`/`MeasureText`/`TruncateText` with a
  `TextStyle` (token size, weight, family, theme color, opacity-based
  disabled) is the ONE text renderer. Input is NFC-normalized for
  presentation only. Width behavior is a typed `TextConstraint`:
  `Intrinsic` carries no width, `Truncate(width)` requires one,
  `Wrap(width, lineHeight?, whitespace?)` owns its optional CSS
  line-height and `TextWhitespace` policy (Normal / PreLine / PreWrap);
  non-positive dimensions are rejected. Presentation normalization also
  canonicalizes CRLF and lone CR to LF, as the HTML parser does. A
  constrained inline run occupies its constraint width in layout so
  siblings flow from the box edge, and carries a typed `TextAlign`
  (Start default; End pins the run's end to the box edge — a truncated
  run keeps its ellipsis there, and a raw overflow shows its end with
  the start clipped, like an end-aligned CSS line). Truncation backs
  off whole grapheme clusters and the renderer CLIPS to the box like
  `overflow: hidden` — when even the ellipsis cannot fit, the ORIGINAL
  run draws through the clip, exactly Blink's narrow behavior; string
  fitting is composition-internal and never a substitute for that clip. Wrapping
  never hard-breaks an over-wide word, accumulates the fractional line
  advance unrounded, half-leading-centers each line, and expands
  preserved tabs to 8-space stops under PreWrap. CJK (Default family
  only — mono and italic stay lean) merges the face Chromium's Segoe UI
  font-link chain falls back to (Meiryo UI before Yu Gothic UI),
  resolved by `WindowsFontFallback` shared verbatim between the game
  and the capture host. Measurement and rendering must share one
  resolved style value.
- UiKernel: `Interactive.Reserve` is the ONE control hit-test — hover,
  press/release, focus, keyboard activation, and the pointer events
  (click, double-click, drag begin/end/delta), ALL occlusion-gated:
  pointer events by pointer occlusion, Enter/Space by keyboard
  OWNERSHIP first (while the exclusive chain is open its TOPMOST link
  owns the keyboard globally: only owners on that link can be
  activated, whether or not the surface covers the control, so an
  ancestor surface cannot be Entered from behind its child and regains
  the keyboard when the child releases; the claim frame counts before
  any rectangle exists) and by rect occlusion for ordinary overlapping
  surfaces, and drags by accepted ownership (a drag begun un-occluded
  ends exactly once; a swallowed press never emits an end). The ONLY
  remaining direct ImGui input queries are these named exceptions, and
  no new widget-local input handling may join them: native-widget
  wrappers (TextInput's InputText focus/hover), popup lifecycle
  (FloatingMenu dismissal, HoverHelp's geometric help hover — the ONE
  help surface), AxisWell's deferred inline-edit block, and Slider's
  pointer-position value math. `Motion` is the ONE animation store —
  one group record per identity (channel set fixed per id, enforced;
  zero-duration snaps), a constant-rate ramp mode, one prune policy;
  BOTH modes reseed rather than advance when the stored frame is not
  strictly behind the current one (same-frame duplicate, or a recreated
  context whose counter restarted); components own no transition
  dictionaries. `ControlSizing.Resolve`
  is the ONE style→logical→scaled resolution preamble. Popovers open
  only through `Crystarium.OpenPopover` (the lower-level primitive is
  internal); popups claim/keep/release the `Interactive` exclusive
  chain only through `FloatingSurface`'s open/sync/release helpers,
  and all floating placement (anchored, point, side-preference) lives
  in `FloatingSurface`. The disabled-help hover gate is
  `HoverHelp.Gate`. These invariants are proven by
  `verify-kernel.ps1` (`--kernel-behavior`), run when kernel code
  changes.
- UI foundation: the active `Theme` value owns colors, typography, metrics,
  radii, shadows, motion, and optical corrections together; a theme change
  installs one complete replacement value rather than mutating tokens.
  The CANONICAL color source is the sibling Picto `tokens.css`;
  `PictoTokens.g.cs` is committed GENERATED output (regenerate with
  `generate-tokens.ps1` — developer-only; production build/load/packaging
  consume the committed file and never need Picto or a generator). Only
  tokens Crystarium consumes are generated. Theme factories and the two
  family builders wire every token-derived field to it; metrics,
  typography, radii, and motion stay typed handwritten members. Six-theme
  color parity is proven by `verify-tokens.ps1` — source-hash drift,
  regeneration diff, and the COMPLETE field mapping (top-level, Chrome,
  Glass, Palette.Primary) with intentional differences classified once as
  extensions — never by rendering six themes.
  The persisted selector mirrors Picto's portable color themes; Auto resolves
  the Windows app mode. Platform window-material themes are out of scope.
  Crystarium is the only product-facing API. Pages supply current state,
  callbacks, and typed `ControlStyle` width/height semantics through Page,
  ActionBar, Section, Form/FormRow and ScrollRegion; compositions resolve
  `Fill` against their allocated region. FloatingSurface alone owns floating
  placement and glass fill, blur, border and shadow.
- Text buttons: `Crystarium.Button` is the Picto action-button family
  (`actionButton.module.css`) with a typed `ButtonVariant` — Secondary
  (`.btn`), Primary (`.btnPrimary`), Danger (`.btnDanger`, CSS-literal
  colors) — never boolean presentation flags; `ControlStyle`'s
  Bare/Selected/Slashed are icon/toggle-only. Geometry: 32px default
  height, 16px horizontal padding, 6px radius, 1px border, label
  centered through the canonical text path and CLIPPED to the visual
  bounds; measurement, drawing, hit testing, the keyboard
  focus-visible outline (2px primary-60, offset 1px), and layout
  reservation resolve from the same rectangle. The background follows
  Picto's 150ms ease hover transition via component-owned transient
  state keyed by stable ImGui identity; borders and text switch
  instantly like the CSS. Activation is release-inside (drag-out
  cancels); Enter/Space activate a keyboard-focused button; pointer
  interaction never shows the focus outline; disabled buttons cannot
  focus or activate, take no hover styling, and keep their HoverHelp
  explanation. `.btn:disabled` is CSS GROUP opacity reproduced through
  the ONE existing drawing path: non-overlapping chrome (fill inset to
  the border's inner edge, the ring carrying the analytically
  flattened border-over-fill color) plus the canonical TextAt label
  with compensated color/alpha — exact for every backdrop when the
  fill is translucent, and surface-referenced (exact over the theme
  surface) for an opaque fill, since affine over-blending cannot
  express a group over an unknown backdrop. There is no second
  rasterizer or texture path. Content width is CSS border-box (label
  + padding + 1px border per side). Compositions forward allocated
  widths into the same component (`ButtonAtWidth`); ActionBar measures
  its own items and resolves Fill against only its remaining
  allocation, never ambient window availability.
- Hover help: `Crystarium.HoverHelp` is the ONE explanatory surface
  (picto KbdTooltip: 400 ms open, instant exit start, the 150 ms Mantine
  pop entering and exiting as one composited surface, glass card on the
  foreground draw list, no input, no layout impact). Controls
  register only stable id + target rect + text (+ shortcut, side);
  `Preview` covers truncation without the delay; the last registration
  of a frame wins, so a semantic row outranks its own wells. No native
  ImGui tooltip may coexist for a migrated target. Form rows use the
  shared label/control/value columns and one semantic density per row.
