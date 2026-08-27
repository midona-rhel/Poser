---
name: ui-standard
description: Poser's control-row and page-composition standard. Load before building or changing any value-editing UI (sliders, wells, pickers, form rows, sections) so the UI stops being reinvented per pane.
---

# The UI standard

These rules govern every generic value-editing surface: form rows of
sliders, numeric wells, dropdowns, pickers, checkboxes, and the pages
that hold them. Bespoke visual surfaces (the bone matrix, the 3D view,
gaze wells, the overlay) are exempt from the width scale but still
follow the reflow, gutter, header, and grouping rules. Compose from
Crystarium primitives — inventing a local layout is a defect.

## Shell roles (the Blender mapping)

Windows each have ONE job. No mode may change what a window IS.

- **Selection means world-manipulable.** Only things you can grab in
  the viewport — actors, bones, lights, cameras, props, overlays —
  are ever "selected". Environment, scene, and library are NOT
  selections; they are property pages and browsers. Treating them as
  selections is a category error.
- **Main window** — the scene editor, always: sidebar = outliner
  (world things, selection), tab strip = properties editor. The strip
  carries the active selection's aspect tabs (Pose/Animation/
  Appearance, Light/Shadows, …) plus the environment's pages as FIXED
  global tabs — always reachable, selection-independent. Opening an
  environment page does not touch the selection, the gizmo, or the
  inspector.
- **The inspector** (right column) carries ONLY the active world
  selection's invariants: summary, reset verbs, rotation gizmo,
  TRANSLATION, TRACKING, IK. Nothing selected → a plain empty state.
  It is user-toggled; its column never appears or vanishes from
  navigation. Aspect content (EXPRESSION, POSE) belongs to tabs,
  never the inspector — nothing is drawn in two places.
- **Library window** — the asset browser: Poses, Auto-saves, MCDFs,
  Scenes, Objects, plus whole-scene save/load. Its own window so it
  stands beside the viewport; it never hijacks the main window. Its
  metadata panel follows the same reserved/toggle policy as the
  inspector.
- **Toolbar** — permanently its own window with a remembered
  position, never a band inside the main window. It has a title bar
  whose ONLY content is a copy of the burger menu; nothing else lives
  there by default. New global buttons earn a place on this toolbar —
  it is the one extensible home, so no other surface grows ad-hoc
  buttons.
- **Pop-outs** are pinned properties: the standard tab-content view
  with a pin that stops it following selection. Identical layout to
  the main tabs — a bespoke pop-out layout is a defect.

## Page composition

- One scrolling page per surface. Sections in PIPELINE ORDER, the same
  skeleton on every surface so muscle memory transfers:
  identity/what-it-is → transform/placement → appearance/specifics →
  files/actions last.
- Standard sections start open; advanced/rare sections start closed.
  The user's open/closed choice is REMEMBERED per section across
  sessions (persisted, not per-session fields).
- Unavailable controls DISABLE IN PLACE with a hover saying why. They
  never vanish. Sections and rows do not appear/disappear with state —
  content changes inside a stable frame.

## Choosing a control

- **Segmented (the pill)** is reserved for navigation between peer
  views or modes — which surface, which tab. It is not a value editor.
  (This is already its de-facto role: 18 of 19 current uses.)
- **Switch** for a single boolean value; label in the label column,
  switch in the control column.
- **Checkboxes grid** for a set of related booleans, aligned on a
  shared column pitch — label-first, always.
- **Dropdown** for one-of-N with known, nameable options (roughly 2–15).
- **SearchPicker** when the option set is large, dynamic, or needs
  search.
- **Visual grid picker** (TexturePicker, Swatches) whenever the choice
  has a visual identity — textures, colors, poses. A name list for a
  visual thing is a defect.

## Width scale

Controls come in exactly two widths. W1 is half of today's effective
minimum control width; W2 = 2 × W1. Every interaction must still WORK
at W1 — readable value, draggable, clickable — and should RESERVE W2
when the cell allows. No third width; no per-pane constants. The
tokens live in Theme, never inline.

- Two elements in a grid or cell always have padding between them
  (`Page.ActionGap`); zero-gap adjacency is reserved for a stepper
  hugging the well it steps (`Spacing.One`).
- Two short related controls may share a row (Pair/Cells) only when
  both keep W1.

## No reflow, ever

Reflow is the enemy. Layout may move for exactly one reason: the user
opened or collapsed a section header. Nothing else — not state, not
selection, not navigation, not a scrollbar — may shift anything.


- Text never reflows a row: values change inside fixed-width wells,
  labels truncate rather than wrap, conditional text is drawn over
  reserved space. Text must not pop into existence and push siblings.
- The scrollbar never reflows the page. Scroll surfaces reserve the
  gutter unconditionally (ScrollRegion does this — use it, never a raw
  child). The bar appearing or disappearing changes pixels, not layout.
- The gutter is symmetric: scrollbar width + its padding equals the
  page's left inset.
- Navigating (tab switches, selection changes) must not shift shared
  chrome — headers, footers, and section frames stay put.
- A control that changes state (enabled, toggled, renamed value) keeps
  its exact footprint.

## Alignment

- Trailing buttons across neighbouring rows align as a column: same
  function → same width, same right edge. Each row sizing its own
  button is a defect.
- Rows in one section share the label column and the control column;
  a control never invents its own column split.

## Sliders

- Travel must match what the value realistically represents. For
  ranges spanning magnitudes (0→1→10→100), use `SliderScale.Log` with
  curvature `10^decades − 1`: 9999 puts 1 at half travel, 10 at
  three-quarters, 100 at the end of a 0–100 range; the default 99
  spends half the travel on the first tenth. No visual indication —
  the mapping is the design.
- The log mapping affects TRAVEL only. Typing and number-dragging stay
  linear.

## Numeric wells (drag-to-change numbers)

- Resting label shows four significant digits (`AdaptiveValueText`):
  one decimal from 100 up, two through the tens, three below ten.
- Drag steps for real-valued wells: 0.1 per unit of drag, Shift = 1,
  Ctrl = 0.01 — that is `perPixel: 0.1`, and the modifier ladder
  (×10 / ×0.1) produces the rest. Integer-id wells may step coarser.
- Double-click to type, always.

## Texture selection

One shape only: the preview tile, then `[−] [id well] [+]` — the well
in the middle, steppers hugging it either side. `TexturePicker.Field`
is the normative implementation; never a bare number row for a
texture id.

## Sections and headers

- The FIRST header on any page draws no leading separator. Use
  `SectionStack` (`divider: stack.Any`) or pass `divider: false` on
  the first `Section` explicitly.
- Section headers, dividers, footers come from PageForm — never
  hand-drawn rules.

## Tooltips

A hover is a label: a few words at most ("Step down one id", "Ctrl
fine ×0.1"). Explanations live in the UI-contract docs and the future
tutorial, never in a tooltip. A sentence-long tooltip is a defect
unless truly exceptional.

## When touching any pane

Check the pane against every rule above, not just the one you came to
change. Compare click-paths with Ktisis/Brio for flow; the visual
standard is THIS document, not theirs.
