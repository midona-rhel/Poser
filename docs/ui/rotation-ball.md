# Inspector rotation ball

## Purpose

The rotation ball is the compact pointer-driven rotation control in the Pose
inspector rail. It edits the same actor or bone transform as the numeric
rotation wells, but exposes direct free rotation and X/Y/Z-constrained gestures.

## Interaction contract

- The red vertical control selects X and a vertical drag changes only X.
- The green ellipse selects Y and a horizontal drag changes only Y.
- The blue lower arc selects Z and a horizontal drag changes only Z.
- A drag beginning on the remaining sphere surface changes X and Y together.
- Shift while free-dragging constrains the gesture to Z.
- The selected axis remains visually emphasized after release; hover and active
  axes use a stronger stroke.
- Releasing any drag commits exactly one history patch through the shared
  clean-gesture session; Escape, selection change, and external cancellation
  restore the frozen baseline exactly once and record nothing.

`PoseRailPane` owns only pointer hit testing and visual axis state.
`PoseInspectorPane.RotateSelection` owns the gesture session, multi-selection
delta transfer, service routing, and history. The inspector's rotation
controls always rotate in place (the toolbar pivot selector governs the
in-world gizmo, not the rail; see `orbit-rotation-design.md`).

## Hit testing

Axis selection is computed in screen pixels from the same geometry that is
painted, at the moment the pointer activates the control:

1. Every eligible axis stroke is measured as a true point-to-stroke screen
   distance: the X segment as distance to the painted vertical line segment,
   the Y ellipse as distance to the painted ellipse stroke, and the Z arc as
   distance to the painted lower arc (angular range included).
2. All eligible axes are compared and the nearest stroke within the shared
   pick tolerance wins. No fixed `if` order may steal an overlap from a
   closer axis.
3. Exact-distance ties resolve in the documented deterministic order
   X → Y → Z. This order applies only to ties.
4. A pointer outside every stroke's tolerance starts a free X/Y drag; points
   outside the ball circle (the reserved item is square) select nothing.

Hover, pointer-down, active drag, retained selection emphasis, tooltip, and
the numeric axis wells that change all name the same axis: the axis is frozen
at activation and drives the entire drag.

## Axis palette

The red/green/blue axis strokes use the shared transform-axis palette that
also colors the toolbar axis wells — one definition, consumed by every
axis-colored surface. Alpha/width emphasis is local presentation state.

## Coordinate convention

The labels are coordinate axes, not the parameter order of
`Quaternion.CreateFromYawPitchRoll`. `PoseMath` therefore maps inspector
`(X, Y, Z)` to `(pitch, yaw, roll)` when calling that API and maps the inverse
conversion back the same way. This ensures red X rotates around `Vector3.UnitX`,
green Y around `Vector3.UnitY`, and blue Z around `Vector3.UnitZ`.

## Reference decisions

- Ktisis' `Gizmo2D` uses a real rotation gizmo whose colored rings are selectable.
  Poser keeps its narrower custom rail rendering but adopts the same constrained
  axis interaction and its nearest-visible-stroke selection rule.
- Brio's transform editor keeps one stable Euler value for the duration of a
  gesture. Poser follows that stability rule in `PoseInspectorPane`.

## Verification

In-game verification must click-drag each colored control at multiple UI
scales, confirm only its matching numeric well changes, confirm hover and
tooltip name the axis that a click would select, release, and verify one-step
undo/redo for both an actor and a bone. Free-space drags change X and Y;
Shift-constrained free drags change only Z.
