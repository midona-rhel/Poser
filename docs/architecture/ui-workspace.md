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
  (undo/redo). Fonts: CSS-size conversion + glyph offset live in
  `FontRegistry`; no per-widget font padding.
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
