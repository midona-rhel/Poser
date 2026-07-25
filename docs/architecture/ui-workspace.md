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
- Pose surface: fixed mode header (Body/Face/Matrix/3D left; Mirror
  selection, Physics, Animation right), middle viewport, fixed footer.
  Matrix is the only scrolling document. Mirror selection (Brio
  `GraphicalSidesSwapped`) swaps sided dots on the maps only.
- Icons: `TablerSvgSources.cs` is generated — never hand-edit;
  `PoserIconSources` wins. Mirrored pairs reuse one glyph with `flipX`
  (undo/redo). Fonts: CSS-size conversion + glyph offset live in
  `FontRegistry`; no per-widget font padding.
