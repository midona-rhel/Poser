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
- Releasing any drag commits exactly one history action through
  `PoseInspectorPane.CommitRotation`.

`PoseRailPane` owns only pointer hit testing and visual axis state.
`PoseInspectorPane.RotateSelection` owns the transform session, multi-selection
delta transfer, service routing, and history.

## Coordinate convention

The labels are coordinate axes, not the parameter order of
`Quaternion.CreateFromYawPitchRoll`. `PoseMath` therefore maps inspector
`(X, Y, Z)` to `(pitch, yaw, roll)` when calling that API and maps the inverse
conversion back the same way. This ensures red X rotates around `Vector3.UnitX`,
green Y around `Vector3.UnitY`, and blue Z around `Vector3.UnitZ`.

## Reference decisions

- Ktisis' `Gizmo2D` uses a real rotation gizmo whose colored rings are selectable.
  Poser keeps its narrower custom rail rendering but adopts the same constrained
  axis interaction.
- Brio's transform editor keeps one stable Euler value for the duration of a
  gesture. Poser follows that stability rule in `PoseInspectorPane`.

## Verification

Live transform scenarios verify that each labeled Euler axis creates a quaternion around
the matching coordinate axis and that Euler round trips remain stable away from
gimbal poles. Visual/in-game verification must click-drag each colored control,
confirm only its matching numeric well changes, release, and verify one-step
undo/redo for both an actor and a bone.
