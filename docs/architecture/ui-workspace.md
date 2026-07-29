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
  (undo/redo). Fonts: CSS-size conversion lives in `FontRegistry` — sizes
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
  siblings flow from the box edge. Truncation backs off whole grapheme
  clusters and the renderer CLIPS to the box like `overflow: hidden` —
  when even the ellipsis cannot fit, the ORIGINAL run draws through the
  clip, exactly Blink's narrow behavior; string fitting is
  composition-internal and never a substitute for that clip. Wrapping
  never hard-breaks an over-wide word, accumulates the fractional line
  advance unrounded, half-leading-centers each line, and expands
  preserved tabs to 8-space stops under PreWrap. CJK (Default family
  only — mono and italic stay lean) merges the face Chromium's Segoe UI
  font-link chain falls back to (Meiryo UI before Yu Gothic UI),
  resolved by `WindowsFontFallback` shared verbatim between the game
  and the capture host. Measurement and rendering must share one
  resolved style value.
- UI foundation: the active `Theme` value owns colors, typography, metrics,
  radii, shadows, motion, and optical corrections together; a theme change
  installs one complete replacement value rather than mutating tokens.
  The persisted selector mirrors Picto's portable color themes; Auto resolves
  the Windows app mode. Platform window-material themes are out of scope.
  Crystarium is the only product-facing API. Pages supply current state,
  callbacks, and typed `ControlStyle` width/height semantics through Page,
  ActionBar, Section, Form/FormRow and ScrollRegion; compositions resolve
  `Fill` against their allocated region. FloatingSurface alone owns floating
  placement and glass fill, blur, border and shadow.
- Hover help: `Crystarium.HoverHelp` is the ONE explanatory surface
  (picto KbdTooltip: 400 ms open, instant exit start, the 150 ms Mantine
  pop entering and exiting as one composited surface, glass card on the
  foreground draw list, no input, no layout impact). Controls
  register only stable id + target rect + text (+ shortcut, side);
  `Preview` covers truncation without the delay; the last registration
  of a frame wins, so a semantic row outranks its own wells. No native
  ImGui tooltip may coexist for a migrated target. Form rows use the
  shared label/control/value columns and one semantic density per row.
