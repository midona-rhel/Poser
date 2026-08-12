# PBI-090 — Interface rhythm and explanatory hover help

## Control

| Field | Value |
|---|---|
| Status | Accepted |
| Size | Large |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-090-base` |
| Feature branch | `feature/pbi-090-interface-rhythm-hover-help` |
| Accepted head | `763c1c1` |

PBI-007 is the implementation baseline. PBI-100 Advanced Expression remains
deferred until a later explicit product decision.

## Outcome

Give the retained Poser workspace one consistent optical rhythm and replace
the current mixture of raw tooltips with Picto's compact animated glass hover
help. Controls explain non-obvious behavior without changing layout, and the
IK form reads as one aligned system rather than individually positioned rows.

## Baseline contract

The current segmented tabs are the accepted visual reference and stay where
they are. Do not shift the font registry, global text renderer, or every text
run to fix components whose manual offsets are wrong.

Centralize component-level optical baselines and apply these observed fixes:

- Sidebar actor/category/bone labels and their badges move up one logical
  pixel.
- Text-button labels move down one logical pixel; icon-only buttons remain
  optically centred independently.
- Segmented-tab labels remain unchanged.
- The pose-footer `Parenting`, `Pos`, `Rot`, and `Scale` labels move up one
  logical pixel and align with their checkbox centres.
- Dropdown trigger/list baselines retain the accepted post-PBI-005 position.

Final draw coordinates snap to framebuffer pixels after applying UI scale.
Do not scatter new `+ 1`/`- 1` literals through consumers; put each optical
offset in its owning primitive or shared layout token.

## Slider styling

Restyle the one shared `Crystarium.Slider` primitive across the retained UI.
The thumb is a solid white circle. The track from its minimum to the current
value is filled with the theme's primary blue; the remaining track stays
neutral. This deliberately supersedes the current Picto transcription's blue
thumb and unfilled track. Preserve the existing geometry, notches, readouts,
disabled opacity, hit area, drag behavior, and value semantics. Do not add
pane-specific slider drawing.

## Transform ordering and naming

The transform inspector uses this order everywhere:

1. **Translation**
2. **Rotation**
3. **Scale**

Rename the current `Position` heading to `Translation` and remove the
`Rotation → Position → Scale` special ordering. This is presentation only;
domain component names and transform math do not change.

## IK form geometry

Replace the IK section's independent 26/28/30-pixel rows and manual offsets
with one shared inspector form-row layout:

- A 94-pixel label column and one remaining control region.
- Normal rows share one height and vertically centred label/control baselines.
- Dropdowns fill the control region without relying on ambient available width.
- Slider rows reserve one consistent right-aligned value column.
- Switches use the same control origin as dropdowns and sliders.
- `Hinge axis` is one normal row: label on the left and three X/Y/Z wells
  across the control region with the standard six-pixel gaps. It is not a
  separate heading followed by a full-width transform scrub row.
- `Live IK` plus `Reset defaults` may remain the single header/action row, but
  its outer padding must match the form beneath it.

The section reports its exact height, has consistent top/bottom gaps, never
overflows horizontally, and does not create a permanent scrollbar.

## Picto hover-help primitive

Implement one shared hover-help renderer matching
`picto/src/shared/ui/KbdTooltip/KbdTooltip.tsx`:

- Open delay 400 ms, close delay 0, six-pixel target offset.
- Mantine's `pop` transition, exactly: OUT is opacity 0, scale(.9),
  translateY(10px); IN is opacity 1, scale(1). Entering runs OUT→IN and
  exiting IN→OUT, each over 150 ms on the CSS `ease` curve
  (cubic-bezier 0.25, 0.1, 0.25, 1) with transform-origin at the card
  centre, applied to the complete composited card (blur, chrome, shadow,
  text, badges). A stable control id prevents animation restart every
  frame.
- Glass background with 16-pixel blur, one-pixel secondary border and glass
  top edge, four-pixel radius, and `0 2px 8px` black at 30% shadow.
- One-line content is 24 pixels high with horizontal padding 6, normal
  13-pixel text, four-pixel content gap, and optional 16-pixel shortcut badges.
- Anchor to the centre of the hovered semantic target on the preferred side;
  flip and clamp to the viewport rather than clipping.
- Render above Poser's windows without taking input or affecting layout,
  scrolling, hover state, or control measurement.
- Only one hover card is visible. Moving directly between controls restarts
  the delay for the new stable id; leaving starts the exit immediately —
  the outgoing card keeps its content and geometry while it reverses, and
  a directly entered target's own 400 ms delay overlaps that exit without
  a second rendered card.
- Disabled controls can still explain why they are unavailable.

The hover card is explanatory, not a duplicate label. For example, hovering
either `Shoulder` or its slider uses the same row target and explains how much
the shoulder participates in the IK solve. Apply equivalent concise help to
the remaining IK fields, transform wells, toolbar actions, pose actions,
parenting controls, and other non-obvious retained workspace controls.

Migrate existing primitive `Tooltip` properties and retained raw
`ImGui.SetTooltip` calls to this renderer. Truncation-only previews may reuse
the same chrome without the 400 ms explanatory delay. Do not show a native
tooltip and a hover card for the same target.

## Architecture and documentation

The primitive owns timing, animation, placement, chrome, and top-layer
rendering. Controls provide only stable id, target rectangle, explanation,
optional shortcut, and preferred side. Do not create a tooltip service per
pane or hard-code IK behavior inside the UI library.

Extend the retained UI contract in `docs/architecture/ui-workspace.md` with a
short baseline/hover-help invariant. Do not create component documentation.

## Excluded

- New poser functionality, gizmo changes, broad copywriting, settings redesign,
  font replacement, DevHost, npm, IPC, screenshots, or a new test framework.
- PBI-100 Advanced Expression implementation.

## Implementation order

1. Add shared optical-baseline tokens and correct sidebar/button/footer text.
2. Normalize transform order/name and build the shared inspector form row.
3. Migrate all IK controls, including the hinge-axis row.
4. Implement the Picto hover-help renderer and primitive integration.
5. Add concise retained-workspace explanations, remove duplicate raw tooltip
   paths, and update the one normative document.

Use reviewable commits without amend or rebase after review starts.

## Acceptance

- At supported UI scales, sidebar text is one pixel higher, button text one
  pixel lower, tabs unchanged, and Parenting labels optically centred.
- Every retained slider has one white circular thumb and a primary-blue filled
  track up to its value, with a neutral remainder and unchanged interaction.
- Transform rows read Translation, Rotation, Scale in that order.
- Every IK row shares label/control/value columns and padding; Hinge axis uses
  the same single-row geometry and all controls remain live.
- Hovering a documented control for 400 ms produces one Picto-matched glass
  card centred on the target with the 150 ms Mantine pop; leaving plays
  the 150 ms exit in reverse.
- Shoulder label and slider produce the same useful explanation. Disabled
  actions and shortcut-bearing toolbar buttons render the appropriate content.
- Hover cards flip/clamp at all window edges, never capture input, never create
  scrollbars, and never coexist with native ImGui tooltips.
- Release is the non-deployment validation gate. A Debug build is only the
  announced deployment action for the exact reviewed head after readiness is
  confirmed; see `docs/process/testing.md`.

## Handoff

Report base/head, commit map, changed paths, centralized baseline decisions,
IK row metrics, migrated tooltip paths, Picto parity details, Release
validation result, deployment decision, and remaining in-game checks.
Compilation is not visual proof.
