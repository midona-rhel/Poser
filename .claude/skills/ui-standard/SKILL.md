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
  search. A picker SHOWS its current value — the picker control is
  the value display and the opener in one; a separate text echoing
  the value beside a picker is a defect.
- **Visual grid picker** (TexturePicker, Swatches) whenever the choice
  has a visual identity — textures, colors, poses. A name list for a
  visual thing is a defect.

## Two widths, nothing between

A content window exists at exactly TWO widths: narrow (one track) and
wide (two tracks, twice the narrow content). A title-bar button
toggles between them — instantly, no animation, no free horizontal
resize. Height stays free; content scrolls.

This is the whole answer to "what happens when the window shrinks":
nothing shrinks. Every band and every row is DESIGNED twice — once
per width — so overlap and crushed controls are impossible by
construction, not prevented by minimum-size guardrails.

- The track is the design unit: W1 = one track's control width, and a
  control never renders below it. Wide = two tracks side by side; a
  full-line row reserves its line and paints one track.
- Two elements in a cell always have padding between them
  (`Page.ActionGap`); zero-gap adjacency is reserved for a stepper
  hugging its well (`Spacing.One`).
- Two short related controls share a line only when both keep W1.
- **One band, one layout**: a band's left and right clusters are laid
  out and measured TOGETHER. Two independently-positioned layers may
  never share a band — overlap is a layout defect, never a
  window-size problem. A cluster that does not fit the narrow width
  gets its own designed band there; it does not get squeezed.

## Width honesty — do the math, it is not optional

- A control is sized to its PROBABLE VALUES, never stretched to fill
  the row. A two-option dropdown does not span 400px; an actor-name
  dropdown needs ~160 logical, not everything left over. Super-wide
  dropdowns and buttons are defects; a shorter control with air
  beside it is correct.
- Freed space CONSOLIDATES: if a right-sized control leaves room,
  pull the next control(s) onto the same line (selector + its verb +
  its lock switch) instead of spending rows.
- Before placing ANY row in the inspector, prove it fits: the
  inspector is 280 logical wide; minus the page insets (12 + 12) and
  the label column (72) the control cell is ~184 at 100% scale.
  A row that overflows there, or forces the inspector to be useless,
  is a placement error — it belongs on a tab instead.

- The verb floor YIELDS: when a row cannot hold all its verbs at the
  standard width, they compress together — never overflow, never wrap.
- Changing ANY global width token re-runs the width math on every row
  that inherits it, the inspector first. The verb floor once overflowed
  the inspector's reset row exactly this way.

## No reflow, ever

Reflow is the enemy. Layout may move for exactly two reasons: the
user opened or collapsed a section header, or the user pressed the
width toggle. Nothing else — not state, not selection, not
navigation, not a scrollbar — may shift anything.


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
- NO ONE-FRAME SETTLE: a view renders its FINAL layout on the first
  frame after navigation. A provisional layout that shifts a frame
  later (measure-then-settle) is a reflow defect even though it is
  fast. Cache or precompute whatever the first frame needs.
- Surfaces that keep their existing design (matrix, actor, 3D) are
  still bound to this contract plus the gutter and the shared page
  padding on all four edges.

## Alignment

- Every verb button on a page has the SAME EXACT width (a fixed verb
  width, not a minimum — "Redraw" and "Reset" render identically), and
  trailing verbs live in one shared right-aligned column page-wide, so
  buttons stacked across rows align to the pixel.
- Rows in one section share the label column and the control column;
  a control never invents its own column split.
- Labels are LEFT-ALIGNED everywhere — form labels, matrix cell
  labels, all of them. Right-aligned labels are a defect.
- Label columns are FIXED WIDTH: every label reserves the same space
  regardless of its text, so control edges align down the page. A
  label the column cannot hold truncates — that is a naming problem.
- Vocabulary: PADDING is space inside a control's own box; MARGIN is
  space between neighbouring boxes. Columns and cells are separated
  by MARGIN (`Spacing.Six`), and two controls are never
  pixel-adjacent unless one is a stepper hugging its well.
- Layout primitives (Pair, Cells, Actions) are audited ONCE, as
  primitives — every consumer inherits their defects. The pair row
  shipped with no inter-cell margin and text-sized label columns, and
  every paired surface inherited both.
- Cell rows (Cells) spread N controls EQUIDISTANT across the row —
  the tint row's Character/Main/Off is the reference.
- FREE CONTROLS — buttons or chips sharing a row with no label column
  (the gaze Eyes/Head parts) — also distribute equally. No label does
  not mean no alignment: the spacing IS the alignment.
- A control's name states EXACTLY what it acts on: "Brow left" moves
  the LEFT brow alone; a control that moves both brows is named for
  both. A precise name on an imprecise control is a defect either way.
- A BIDIRECTIONAL slider is its motion axis, so the motion word leaves
  the name: "Jaw Open" (−1…1) is "Jaw", "Brow Up" is "Brow", the lip
  halves are "Upper lip"/"Lower lip". A distinctive verb (Furrow)
  survives as the name.
- Expression rows pair L/R (and Upper/Lower) on one row in EVERY
  host, the inspector included. Region mini headers in the inspector
  were tried 2026-08-27 and rejected — do not reintroduce them.
- The label's fixed column keeps a trailing margin (`Spacing.Three`)
  inside it: text truncates into breathing room, never against its
  control.
- A fixed header over a scrolling surface (the bone filter over the
  matrix or the body/face maps) breathes one gap off the surface top
  and closes with a separator, so what stays put is legible.
- Short rows PAIR two-up by design where it halves a section's height:
  Override|Weather, Swimming|Depth, Opacity|Tint. Pairing is a
  deliberate per-section choice at the design width, not a responsive
  behavior; selector rows and field rows keep full rows.

## Sliders

- The classic track + readout well IS the slider. The value-well
  slider (fill inside a well) was tried in-game 2026-08-27 and
  REJECTED — the web mockup approved it, the real render did not.
  Do not reintroduce it without a new in-game verdict.
- Travel must match what the value realistically represents. For
  ranges spanning magnitudes (0→1→10→100), use `SliderScale.Log` with
  curvature `10^decades − 1`: 9999 puts 1 at half travel, 10 at
  three-quarters, 100 at the end of a 0–100 range; the default 99
  spends half the travel on the first tenth. No visual indication —
  the mapping is the design.
- The log mapping affects TRAVEL only. Typing and number-dragging stay
  linear.

## Numeric wells (drag-to-change numbers)

- Resting label shows four significant digits (`AdaptiveValueText`),
  and the generic well (`Form.ValueColumnWidth`) is sized to fit them
  — widen the token, never per-pane.
- Drag steps for real-valued wells: 0.1 per unit of drag, Shift = 1,
  Ctrl = 0.01 — that is `perPixel: 0.1`, and the modifier ladder
  (×10 / ×0.1) produces the rest. Integer-id wells may step coarser.
- Double-click to type, always.
- Numbers render in the MONO family (Cascadia), never Segoe:
  proportional digits make a changing value wiggle inside its well —
  the no-reflow rule at glyph scale. Applies to every numeric readout,
  well, and matrix cell.

## Row patterns

- **Override switch**: a Switch row named "Override" leads; its
  dependent rows follow immediately, DISABLED (never hidden) while
  the override is off. The wet-surface section is the reference.
- **Selector row** (external ownership): current value + a select
  action + a reset action, with the owned state shown. One shape for
  everything picked from elsewhere (model, glamour, customize).
- **Explicit-apply field**: a raw id/text field applies on its action
  button, never on keystroke.
- **Status row**: one-line state ("This actor is no longer
  available.") drawn as a form row, not ad-hoc text.

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

A hover is a short WHAT-IT-DOES phrase: "Preview the expression",
"Reset the face bones", "Fade the whole actor" — a few words, verb
first. Explanations live in the UI-contract docs and the future
tutorial, never in a tooltip. A sentence-long tooltip is a defect
unless truly exceptional.

## How to design a surface

Rules alone do not produce a good page. Before building or changing
any surface, sit with it and answer these, in writing, for BOTH
widths:

1. **Inventory.** List every band and every control the surface
   needs. Nothing gets placed before everything is listed.
2. **Does this need its label?** If the section header or the
   neighbouring control already says what it is, the label is noise —
   drop it. A well under a "Tint" header does not need "Color".
3. **What carries the row at narrow?** For each band: does it fit one
   track? If not, what is the designed narrow form — its own band, a
   dropdown instead of segments, icons instead of words? "It
   truncates" is not a design.
4. **Where is the empty space at wide?** Point at every blank region
   and say whether it is deliberate reservation (a full-line row's
   empty track) or waste (a lone half-row that should share its
   line). Waste means re-pair the rows.
5. **What changes state, and does its footprint hold?** Walk every
   toggle, override, and selection change; the layout must not move.
6. **Same function, same width.** Trailing buttons across rows form
   one equal-width column; reset verbs look the same everywhere.
7. **What would Blender do?** Check the analogous editor; deviate on
   purpose or not at all.

Write the two designs down before writing code. If a control has no
good narrow form, the surface is not done being designed.

## When touching any pane

Check the pane against every rule above, not just the one you came to
change. And when the in-game pass surfaces a refinement, write it into
THIS skill the same session — the skill is only normative while it is
current. Compare click-paths with Ktisis/Brio for flow; the visual
standard is THIS document, not theirs.
