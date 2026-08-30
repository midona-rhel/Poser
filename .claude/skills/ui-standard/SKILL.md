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
  Scene selector (text segments, in the TITLEBAR, docked on
  the CONTENT side of the content/inspector divider — it stands over
  what it swaps) chooses
  between the selection's tabs, the environment under ITS OWN four-tab
  strip — Lighting (Time, Weather, Lighting: how the scene is lit,
  the most-used controls first), Sky (Sky, Stars), Atmosphere (Fog,
  Rain, Particles, Wind), World (Rendering, Festivals). Big surfaces
  split into tabs by SUBJECT, no one-section orphan tabs, most-used
  first — the old scattered five-way split was rejected. And the
  whole scene workspace (save/load, options, progress, plus the way
  into the library). Selecting ANY entity snaps the content back to
  Target. The mode persists in config. The selector HIDES while the
  window is collapsed, and its FIRST SEGMENT IS the selection's kind
  — Actor, Object, Camera, Light, Overlay, or Target when nothing is
  selected — in a FIXED slot sized to the widest kind name, so a
  selection change never moves a pixel of the band. The kind follows
  the SELECTED OBJECT always, whichever panel is active — it names
  what Target would show, never the active panel.
- **Library** — its OWN window, never a mode of the main window:
  opening it replaces nothing (shipped once as a full-width main
  workspace with an inspector-styled rail; both rejected 2026-08-30).
  It stands on the WORKSPACE ground — the darker coat the content
  well wears — never the panels' raised glass, and its bar ends in
  the shell's own order — the COLLAPSE chevron is ALWAYS the
  rightmost button in any title bar, the close to its left.
  Reached from the sidebar titlebar's library button, the Scene
  panel's "Open library…" verb and the burger menu. A TITLEBAR
  carries a title and a close, nothing else — the type strip gets
  its OWN band below it (corrected 2026-08-30), leading with the
  tabs that can PREVIEW (Poses, Auto-saves) and standing the
  file-info tabs (MCDF, Scenes) at the far right. The right column is
  PERMANENT (224 logical, a separator rule off the navigator — the
  library has NO inspector and nothing styled as one): the preview
  for the types that can preview, and the file's metadata — as much
  as the entry knows — for the rest. There is NO footer (an
  options-band footer shipped and was rejected the same day,
  2026-08-30): the import options hide behind the library's OWN
  options menu — the SAME standing settings the import flow reads
  (one retained state), but options only: reusing the import menu
  one-to-one shipped and was rejected the same day (its actions do
  not belong in the library). The preview CONTAINER owns the seat:
  the options button at the column's left with the Preview title
  moved right beside it, one header band — the column itself never
  moves or shrinks for it (a carved gutter shipped and was rejected
  on sight, 2026-08-30) — and the menu opens leftward from it: over
  the navigator, never over the preview, which stays visible while
  the options are worked. The
  seat exists ONLY for the types that have import options — no
  options, no button.
- **The sidebar never hides.** No collapse, no chevron — the
  hide-sidebar button shipped and was rejected the same day
  (2026-08-30). The only window-management verbs are pop out,
  detach and merge. The scene tree is ONE continuous list — the
  ACTORS/OBJECTS/LIGHTS/CAMERAS/OVERLAYS headers are gone (kind
  order retained: actors, objects, lights, cameras, overlays), and
  the filter pill spans the SAME width as the rows below it. The
  footer's adopt-glyph band carries the spawn PLUS right-aligned on
  the same line: adopt brings the world's things in, the plus adds
  anything new.
- **The cell never duplicates the toolbar.** The sidebar titlebar
  cell carries the brand (no GPose pill), the burger LEFT-aligned
  by the brand, and the library button — a TEXT button reading
  "Library", right-aligned — nothing else.
- **TWO SIDEBARS, ONE CONTRACT.** The sidebar exists in TWO hosts —
  the merged shell's cell and the detached Scene window — and EVERY
  sidebar-chrome change lands in BOTH in the same pass. This has
  been missed twice (icon button 2026-08-30, text button the same
  day): before finishing any sidebar change, open SplitShellWindows
  and apply it there too. Undo, redo, spawn, the
  GPose pill and the gizmo clusters live on the toolbar window
  alone; a second copy in the titlebar is a defect (shipped once,
  overlapping, 2026-08-30). Detaching the shell never repositions
  the toolbar — it is its own window with its own remembered place.
- **Naming** — the detached selection window is the PROPERTIES
  window internally; the user just sees the name of what they have
  selected as its title. The right rail is the INSPECTOR.
- **Collapse everywhere** — every shell window collapses to its
  title bar with the chevron and remembers its height (main window,
  library; Settings is the ONE exception).
- **Toolbar** — permanently its own window with a remembered
  position, IMPLEMENTED 2026-08-28: always open with the shell,
  attached or detached, no reattach affordance, and the main
  titlebar never hosts the gizmo cluster. It carries the brand,
  burger, undo/redo, spawn, project, and the gizmo clusters; new
  global buttons earn a place there.
- **Spawn browser (the portal)** — the add-to-scene window. Its kind
  strip is the MIXED segmented variant: the kinds wear their icons
  (six text tabs made the window super wide) and only "All" keeps
  its word — short, and no glyph says it better; the kind names
  survive as the icon tabs' hovers. The window is built around the
  strip, so the icons are also what makes it compact — and the strip
  SPANS its row at a fixed width, each tab taking an equal share of
  the slack: a natural-width icon strip left a small island in a
  wide band, which read as broken (2026-08-30).
- **The anonymous group** — selecting two or more entities of ANY
  kinds is a group that was never created: one Selection page (kind
  counts, Move to camera, Deselect), one gizmo seated at the LIVE
  centroid of the members with a world-aligned frame, rotation and
  scale about that one point (Centroid pivot), translation carrying
  it. Cameras and overlays ride along untargeted. Selecting a single
  member edits it individually, exactly as in a named group.
- **Named groups** are NAMING AND STRUCTURE over the anonymous
  group: one depth only (a group never contains a group), one home
  per entity, folder rows first in the tree with members nested one
  level in, and a group row's click selects the whole membership —
  the multiselect machinery does everything else. The Selection page
  and titles wear the group's name when the selection IS the group;
  its NAME is the page's first field, edited inline (the camera's
  own pattern); Group… and Ungroup live on that page, and Ungroup
  dissolves without destroying. PARENT AND CHILDREN NEVER SELECT
  TOGETHER: members multiselect freely within a group, but the head
  is only ever selected alone (it IS the whole membership), and only
  one of the two levels wears the pill at a time — actor bones are
  the ONE exception, keeping their dual actor-and-bones highlight.
- **Tree drag-and-drop** — entity rows and group heads drag (never
  bones or categories); a held press that travels is the drag, the
  release is the drop, and the row under the pointer is the live
  candidate. INTO exists only where a child can actually land: a
  group head, and never from another group head (one depth) — those
  rows split into thirds (before/INTO/after); every other row splits
  at its midline into before/after, and NEVER lights up as a
  container. The indicators (2026-08-30): INTO is the accent row
  fill; an insert is an accent CARET LINE at the exact seam, a small
  triangle at its head; plain hover goes SILENT for the drag's
  duration — during a drag only the drop indicators speak. Dragging
  a selected row carries the whole entity selection, and the ghost
  says so ("3 selected", not the one row's name). Open space
  un-groups. The ROOT list is the USER'S order (ruled 2026-08-30,
  overriding the earlier kind-order): entities and group heads
  re-seat anywhere, kinds interleaved; a new spawn lands at the
  bottom. ATTACHED rows never drag — a companion rides its owner, a
  bone-attached light rides its bone; their rows still stand as drop
  seams but hold no grip.
- **State marks speak selected-accent** — a row's "this is the
  current one" fact (the game's target actor, the live camera) is
  the accent-SELECTED mark on its action-strip glyph, never a text
  badge. The "Live" camera badge was removed 2026-08-30 for exactly
  this; "Default" remains a badge because it is identity, not
  state.
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
  the label column (90) the control cell is ~166 at 100% scale.
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
  fast. Cache or precompute whatever the first frame needs. Mode and
  navigation STATE reads take one SNAPSHOT per frame: a control that
  writes state mid-draw (the titlebar selector) must not have later
  draw code read the new value in the same frame — everything flips
  together next frame.
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
  font changes (Roboto ran wider than Segoe, 72 became 84; Geist runs
  wider than Roboto, 84 became 90 and the verb width 63 became 69,
  2026-08-30).
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
- In a window that DOES one thing, the verb leaves the row: every
  portal row adds, so "New spot light" is "Spot light" — half the
  rows had already dropped the prefix, and a list that says "New"
  five times is chrome talking to itself.
- A control's name states EXACTLY what it acts on: "Brow left" moves
  the LEFT brow alone; a control that moves both brows is named for
  both. A precise name on an imprecise control is a defect either way.
- A BIDIRECTIONAL slider is its motion axis, so the motion word leaves
  the name: "Jaw Open" (−1…1) is "Jaw", "Brow Up" is "Brow", the lip
  halves are "Upper lip"/"Lower lip". A DISTINCTIVE verb (Furrow,
  Pucker) survives AS the name — the rule drops the generic word,
  whichever position it sits in, never blindly the second one ("Lip
  Pucker" is "Pucker", not "Lip"). And two controls may never
  collapse to the same name — a collision proves the wrong word was
  dropped ("Lip Pucker" and "Lip Open" both became "Lip",
  2026-08-30).
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
  Orthographic|Ortho zoom, Follow|Lock, Freeze water|Restore water,
  Sections|File. Pairing is a
  deliberate per-section choice at the design width, not a responsive
  behavior; selector rows and field rows keep full rows. Pair flow is
  applied by READING every row it will wrap — a Cells row is already
  a designed multi-cell row and always keeps its full line (the
  primitive enforces this since the Stars incident, 2026-08-30);
  blanket-pairing a whole section without reading its rows is how
  that incident happened.

## Sliders

- The classic track + readout well IS the slider. The value-well
  slider (fill inside a well) was tried in-game 2026-08-27 and
  REJECTED — the web mockup approved it, the real render did not.
  Do not reintroduce it without a new in-game verdict.
- Travel must match what the value realistically represents. For
  ranges spanning magnitudes (0→1→10→100), use `SliderScale.Decades`
  (curvature = the decade count): LINEAR from the minimum to
  max/10^decades across the FIRST HALF of the travel, then one decade
  per equal remaining segment — 0→1 to the middle, 10 at
  three-quarters, 100 at the end. (Corrected 2026-08-28: the pure
  exponential Log-9999 reading of this spec was wrong — the first
  segment is linear.) No visual indication — the mapping is the
  design.
- The log mapping affects TRAVEL only. Typing and number-dragging stay
  linear.

## Numeric wells (drag-to-change numbers)

- Resting label shows four significant digits (`AdaptiveValueText`),
  and the generic well (`Form.ValueColumnWidth`) is sized to fit them
  — widen the token, never per-pane.
- Drag steps for real-valued wells: 0.1 per unit of drag, Shift = 1,
  Ctrl = 0.01 — that is `perPixel: 0.1`, and the modifier ladder
  (×10 / ×0.1) produces the rest. Integer-id wells may step coarser.
  SLIDER READOUT WELLS obey the same ladder: their rate caps at 0.1
  (only a tighter range drags finer) — a range-derived rate like
  (max−min)/300 is a unique scaling and a defect.
- Double-click to type, always.
- Alt-click resets a VALUE to its stated default (`altReset`) — one
  gesture, one undo step, sliders and wells alike: the track, its
  readout well, bare number wells and the transform grid all speak
  it (rotation resets to 0, scale to 1, camera lens facts to 0).
  Wire it wherever a value has a meaningful default; a value with
  none (a world position) stays inert rather than resetting
  somewhere absurd. The default is the OWNERSHIP BASELINE, not a
  constant: the values the entity carried when Poser took it —
  spawn, clone, or a file/scene apply — re-captured at every
  ownership moment and kept in memory (the camera's
  CaptureOwnedDefaults is the reference).
- NO control listens to the scroll wheel — the wheel belongs to the
  page scroll, and a well that stepped its value on a notch hijacked
  it (the Brio wheel-stepping was removed 2026-08-30). The pose
  preview and the 3D canvas keep their wheel zoom: they are VIEWS,
  not value controls.
- Numbers render in the MONO family (Geist Mono), never the text face:
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

- A section TITLE is part of every contained control's identity:
  a title that changes while the user types in the section (a name
  field renaming its own header) resets the field's focus after one
  character and hands the keyboard to the game (group rename,
  2026-08-30). Titles are STABLE; live values belong in fields.

- Headers are SENTENCE CASE — "Wet surface", never "WET SURFACE".
  Only the first letter capitalizes; acronyms (MCDF, IK, 3D) keep
  their caps.
- The FIRST header on any page draws no leading separator. Use
  `SectionStack` (`divider: stack.Any`) or pass `divider: false` on
  the first `Section` explicitly.
- Section headers, dividers, footers come from PageForm — never
  hand-drawn rules.

## Icons

A latched or emphasised glyph uses Tabler's OWN filled twin
(<name>-filled in the sources), rendered through the one icon
pipeline. Hand-rolling a filled shape under an outline shipped once
(the favourite star, 2026-08-30) and read as nonsense — land the real
filled source instead. And filled sources render through the SAME
supersampled coverage mask strokes use: the direct triangulated fill
had no anti-aliasing, which is why pin-filled and pause-filled read
as the worst glyphs in the app until 2026-08-30. And a kind's glyph is stated ONCE: the spawn
strip's Objects tab wears the same Diamond its object rows wear.

## Spawned surfaces

A click-spawned surface (menu, picker, popover) ANCHORS to what
spawned it: its top-left edge aligns to the initiating control's
bottom-left. When that does not fit, flip — the surface's bottom-left
to the control's top-left — and when neither fits, fit it wherever it
can. A surface spawned from a MENU row anchors the CLICK itself (the
row is gone by the time it opens); the pickers' default last-item
anchor covers everything opened from a form control.

## Modifier roles

Modifier roles belong to the CONTRACT, never to a setting: Shift and
Ctrl are the drag ladder in the UI and the speed pair in flight, Alt
is the visibility peek — exclusively and everywhere. A dropdown that
let a user reassign a modifier's role shipped once (the Hold-to-
suspend pair) and was removed 2026-08-30: configurable roles are how
collisions come back. Gizmo snapping holds its OWN keys — Z for step
snap, X for surface snap — because Ctrl and Shift are the step
ladder during the very drags snapping applies to.

## Search fields

Every search or filter field's placeholder is the one word "Search" —
never "Search poses", "Filter bones", "Search everything spawnable".
The field's surroundings already say what is being searched; naming it
again is chrome talking to itself (swept 2026-08-30).

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
8. **Measure the four margins.** The trailing gutter counts as the
   right margin — content runs to the scroll region's edge and the
   gutter provides the space. A page inset stacked on the gutter
   doubles the right edge; rules in prose catch nothing — MEASURE
   the two edges on every surface you touch.

Write the two designs down before writing code. If a control has no
good narrow form, the surface is not done being designed.

## When touching any pane

Check the pane against every rule above, not just the one you came to
change. And when the in-game pass surfaces a refinement, write it into
THIS skill the same session — the skill is only normative while it is
current. Compare click-paths with Ktisis/Brio for flow; the visual
standard is THIS document, not theirs.
