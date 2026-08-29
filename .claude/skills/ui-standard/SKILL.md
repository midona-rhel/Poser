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
(Settled 2026-08-28: the inspector-mode redesign.)

- **Selection means world-manipulable.** Only things you can grab in
  the viewport — actors, bones, lights, cameras, props, overlays —
  are ever "selected". Environment, scene, and library are NOT
  selections.
- **Main window** — the scene editor, always: sidebar = OUTLINER
  (world things only — the library, scene, and environment section
  headers left it), tab strip = the selection's aspect tabs and
  nothing else.
- **The INSPECTOR is only ever the selected object.** Nothing swaps
  it — not modes, not pages. (The selector was first put on the rail
  and corrected 2026-08-28: it swaps the LEFT side.)
- **The CONTENT side is a three-panel area**: a Target | Environment |
  Scene selector (text segments, in the workspace band beside the
  Animation/Physics toggles, measured with them as one band) chooses
  between the selection's tabs, the whole environment (every section
  on one page — Time and Weather open, the rest closed), and the
  whole scene workspace (save/load, options, progress, plus the way
  into the library). Selecting ANY entity snaps the content back to
  Target. The mode persists in config.
- **Library** — the full-width workspace: the outliner stands down
  while it is open and returns with the scene editor. Reached from
  the Scene panel's "Open library…" verb and the burger menu; its
  type strip and per-type metadata rails stay.
- **Toolbar** — permanently its own window with a remembered
  position; its title bar carries only the burger-menu copy, and new
  global buttons earn a place there.
- **Pop-outs** are pinned properties: the standard tab-content view
  with a pin. A bespoke pop-out layout is a defect.

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
  content changes inside a stable frame. The inspector's verbs band is
  the reference: every selection reserves the same two-verb row
  (reset + Select children), disabled where inapplicable, so
  navigating between selection kinds never reflows the rail.

## Choosing a control

- **Segmented (the pill)** is reserved for navigation between peer
  views or modes — which surface, which tab. It is not a value editor.
  (This is already its de-facto role: 18 of 19 current uses.)
- **Switch** for a single boolean value; label in the label column,
  and the toggle RIGHT-ALIGNS in its cell with ONE TOGGLE-WIDTH of
  right margin — every switch site (plain rows, pair cells, action
  rows) seats it there.
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

## Width honesty — do the math, it is not optional

(The fixed two-widths doctrine was proposed and REJECTED 2026-08-27:
windows resize freely; overflow is made impossible by honest control
widths and the yielding verb floor, never by pinning window sizes.
Narrow-form designs are parked for a future effort.)

- A control is sized to its PROBABLE VALUES, never stretched to fill
  the row. A two-option dropdown does not span 400px; an actor-name
  dropdown needs ~160 logical, not everything left over. Super-wide
  dropdowns and buttons are defects; a shorter control with air
  beside it is correct.
- Freed space CONSOLIDATES: if a right-sized control leaves room,
  pull the next control(s) onto the same line (selector + its verb +
  its lock switch) instead of spending rows.
- Inside a CELL, the control FILLS the cell: the cell is the
  honestly-sized unit, and a minimum-width well adrift in a wide cell
  reads as broken. Width honesty sizes the cell; the control takes it.
- A destructive whole-set verb is ALWAYS the danger color, armed or
  not — red is the warning, arming is the guard.
- Before placing ANY row in the inspector, prove it fits: the
  inspector is 280 logical wide; minus the page insets (12 + 12) and
  the label column (72) the control cell is ~184 at 100% scale.
  A row that overflows there, or forces the inspector to be useless,
  is a placement error — it belongs on a tab instead.
- The math also VETOES pairing: a rail pair cell is ~55 logical of
  control after its label — sliders cannot live there, so the IK
  sliders stay single rows in the rail. Pair on the wide surface,
  single on the rail (gaze Mode|At is the reference).

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
  regardless of its text, so control edges align down the page.
- A TRUNCATED LABEL NEVER SHIPS. Truncation is a safety net, not a
  state: when a label ellipsizes, fix it on sight — rename it to fit,
  merge the row (a "Sections" label beside a "Release all sections"
  button is saying it twice), or re-size the column token. The column
  is sized so the app's REAL labels fit, and re-measured whenever the
  font changes (Roboto runs wider than Segoe; 72 became 84).
- Vocabulary: PADDING is space inside a control's own box; MARGIN is
  space between neighbouring boxes. The UI standardizes on MARGIN for
  everything inter-box: columns and cells (`Spacing.Six`), label to
  control (`Spacing.Three`). Two controls are never pixel-adjacent
  unless one is a stepper hugging its well.
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
- The SURFACE and the INSPECTOR are different designed forms of the
  same data, and a change to one must never splash into the other.
  Surface expression rows pair L/R, Upper/Lower, and leftover singles
  (Jaw | Lip), and EVERY surface slider shows its numeric value. The
  inspector shows ONE BARE slider per row — no numeric wells, the
  generic reset as its only verb. Region mini headers in the
  inspector were tried 2026-08-27 and rejected — do not reintroduce
  them.
- The drag hover is "Drag · double-click to edit" — nothing longer.
- MARGIN-FIRST: all spacing between sibling boxes is a margin owned
  by the LAYOUT — the control column starts `Spacing.Three` after the
  label column in every row, pair half, and cell. Padding exists only
  between a box's border and its own content (a well's text inset, a
  button's label inset). Space is never carved out of a neighbour's
  band.
- A fixed header over a scrolling surface (the bone filter over the
  matrix or the body/face maps) breathes one gap off the surface top
  and closes with a separator, so what stays put is legible.
- Short rows PAIR two-up by design where it halves a section's height:
  Override|Weather, Swimming|Depth, Opacity|Tint, Speed|Sensitivity,
  Orthographic|Ortho zoom, Follow|Lock. Pairing is a
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
- Alt-click resets a slider to its stated default (`altReset`) — one
  gesture, one undo step. Wire it wherever a row has a meaningful
  default (expressions reset to zero).
- Numbers render in the MONO family (Roboto Mono), never the text face:
  proportional digits make a changing value wiggle inside its well —
  the no-reflow rule at glyph scale. Applies to every numeric readout,
  well, and matrix cell.

- A destructive whole-set verb (Destroy all) requires an armed
  confirmation — the first press arms ("Confirm destroy all"), the
  second executes. Camera's Destroy all is the reference.
- DESTROY is the one destruction verb for spawned things (props,
  lights, cameras) — Delete and Remove are invented synonyms.
  RELEASE is for borrowed world things: releasing gives back and
  loses nothing, so it neither arms nor wears the danger color.

## Row patterns

- **Override switch**: a Switch row named "Override" leads; its
  dependent rows follow immediately, DISABLED (never hidden) while
  the override is off. The wet-surface section is the reference.
- **Selector row** (external ownership): current value + a select
  action + a reset action, with the owned state shown. One shape for
  everything picked from elsewhere (model, glamour, customize).
- **Explicit-apply field**: a raw id/text field applies on its action
  button, never on keystroke.
- **Status row**: one-line STATE ("This actor is no longer
  available.", "3 objects will go.") drawn as a form row, not ad-hoc
  text. A standing explainer sentence that is always true ("objects
  last for this session") is tutorial content and never lives in the
  UI — the contract docs and the future tutorial own it. An armed
  destroy-all's warning ENUMERATES what goes ("3 objects will go."),
  never speaks in the abstract.

## The transform grid (inspector)

Transform presentation is UNIVERSAL: every inspector that shows
position-like vectors uses the SAME grid composition — the actor's
translate/rotate/scale, the camera's Position or Offset/World
position — with its own row set and icons. AxisVector word-label rows
in an inspector are a defect.

The inspector's transform is a 3×3 GRID, not three labelled rows:
rows are translate / rotate / scale wearing the TOOLBAR'S icons (the
same glyphs, so the tool and its numbers read as one thing); columns
are the axes, each wrapped in its color-coded rounded box (AxisX/Y/Z
palette) that rises above the grid with extra-round top corners and
carries its letter in a CUTOUT of the top border — a fieldset legend.
The wells inside carry no letters; the column says it once.
`Crystarium.TransformGrid` is the normative implementation. The grid
explains itself — its section carries NO header, and the grid ends in
its own bottom margin. The icon column is small (18 logical) so every
well FULLY fits six digits and a dot; rotation shows one decimal
(degrees — four digits say everything), the metric rows keep
thousandths.

Hover registrations gate on the pointer being inside the rect —
unconditional HoverHelp.Explain calls fight each other and the last
registration wins everywhere. And a row's help anchors on its LABEL
band, never the whole row — a bigger hover region must never hide a
control's own hover. The label explains the row; each control
explains itself. On a CELLS row the shared help anchors on EACH
cell's label band (the row has no label of its own) — the hover is
always tied to exactly what the pointer is on, and one wide hover
spanning several controls is a defect wherever it appears.

## The rotation ball

Every world selection with an orientation shows an orientation
control of the SAME footprint in the inspector, so navigating between
them never reflows the rail. Bones and actors get the rotation-ring
ball. A camera gets the JOYSTICK ORB instead — ring semantics were
tried on the camera 2026-08-28 and rejected: the DISC is a joystick
(grab anywhere inside, leniency by design; deflection pans the camera
at a deliberate rate; the knob springs home on release), and the
WHITE RING drags camera roll directly.

## Texture selection

One shape only: the preview tile, then `[−] [id well] [+]` — the well
in the middle, steppers hugging it either side. `TexturePicker.Field`
is the normative implementation; never a bare number row for a
texture id.

## Sections and headers

- Headers are SENTENCE CASE — "Wet surface", never "WET SURFACE".
  Only the first letter capitalizes; acronyms (MCDF, IK, 3D) keep
  their caps.
- The FIRST header on any page draws no leading separator. Use
  `SectionStack` (`divider: stack.Any`) or pass `divider: false` on
  the first `Section` explicitly.
- Section headers, dividers, footers come from PageForm — never
  hand-drawn rules.

## Tooltips

A hover is a short WHAT-IT-DOES phrase: "Preview the expression",
"Reset the face bones", "Fade the whole actor" — a few words, verb
first. Every labelled control HAS one; a value's units belong in it
("Orbit above or below, degrees"). Explanations live in the UI-contract docs and the future
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
