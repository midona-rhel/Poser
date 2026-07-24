# Inspector rotation gizmo

## Purpose

The compact rotation control in the Pose inspector rail is an **oriented 3D
rotation gizmo**, not a flat screen-space symbol (PBI-002 runtime round 1
superseded the earlier flat ball). It edits the same actor or bone transform
as the numeric rotation wells through the same clean gesture, and is the
conceptual sibling of Ktisis `Gizmo2D` and Brio `ImBrioGizmo.DrawRotation`.

## Rendering contract

- Three complete X/Y/Z circles are generated in 3D and projected through the
  active game camera's rotation (orientation only — the widget has fixed
  size and no perspective; the camera view matrix is decomposed to its
  rotation and X-mirrored for the game's view handedness, Brio's
  convention).
- In **Local** mode the rings are oriented from the selected bone's
  **parent frame** — the frame the displayed parent-local rotation and the
  numeric X/Y/Z wells live in — so the red ring always rotates about the
  same X the X well edits (round-1 user feedback: rings oriented from the
  bone's own rotated axes drifted apart from the wells). The parent is
  frozen during a gesture; actors, having no parent, use the world frame.
  In **World** mode the rings are world axes viewed through the camera.
  Rotating the camera or the parent visibly changes the arcs and circle
  foreshortening.
- Front-facing ring segments use the shared transform-axis palette at full
  strength; rear-facing segments use the same hue at a restrained low alpha,
  so every ring stays legible as a complete circle. Front segments draw over
  rear segments.
- A **wide outer roll ring** — slightly larger than the axis rings — rolls
  the target about the axis the camera points along, always applied in the
  world frame (round-1 user request).
- During a drag the rings rotate live with the accumulated delta so the
  widget visibly follows the gesture like the in-world gizmo; on release
  they re-derive from the static frame (a parent-frame widget snaps back by
  construction, since rotating a bone does not move its parent).

## Interaction contract

- Hit testing picks the **nearest visible (front-facing) projected ring
  segment** within the pick tolerance, measured in screen pixels against the
  same geometry that is painted. Exact-distance ties resolve in the
  documented deterministic order X → Y → Z (ties only).
- Hover marks the grab point in the axis color, names the axis in the
  tooltip, and always agrees with the axis a press would drag and the
  quaternion a drag applies.
- A drag projects mouse movement onto the grabbed ring's frozen screen
  tangent (~200 px per radian) and composes an axis-angle quaternion about
  that ring's axis. The applied value is always the TOTAL rotation from drag
  start, dispatched against the clean gesture's frozen baseline — raw screen
  X/Y deltas are never mapped to Euler components, and no frame feeds a
  native result back as the next frame's baseline.
- The shared drag-modifier policy applies (Ctrl fine 0.1×, Shift coarse
  10×, Ctrl+Shift 1×). The mouse wheel is never consumed and never edits —
  it keeps scrolling the inspector rail.
- Local applies the delta in the parent frame (pre-multiplying the frozen
  drag-start local rotation); World conjugates the delta through the frozen
  parent rotation so it acts about world axes.
- One drag produces one clean gesture and one history item via
  `PoseInspectorPane.RotateSelectionGizmo`/`CommitRotation`; Escape,
  selection change, and external cancellation restore the frozen baseline
  exactly once and record nothing, with restart suppressed until the pointer
  releases. No second transform state machine exists.

`PoseRailPane` owns only projection, hit testing, and per-drag tangent
state. `PoseInspectorPane` owns the gesture session, target resolution,
service routing, and history — identical to the numeric wells.

## Reference decisions

- Brio `ImBrio.Gizmo.DrawRotation` supplies the ring generation,
  camera-rotation projection, front/back split, and tangent-projection drag
  math (Ktisis `Gizmo2D` embeds stock ImGuizmo and has no ring source).
- Brio's wheel-to-rotate and right-click axis lock are deliberately not
  ported: the wheel is navigation, and axis choice is hover-based.

## Verification

In-game at multiple UI scales: rotate the camera and confirm the rings
foreshorten; switch Local/World and confirm ring orientation follows the
documented frame; drag each ring and confirm the matching numeric well
changes and hover/tooltip/applied axis agree; confirm a wheel over the gizmo
scrolls the rail; verify Ctrl/Shift drag sensitivities and one-step
undo/redo for both an actor and a bone.
