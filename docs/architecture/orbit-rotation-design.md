# Orbit rotation — rotating a bone around a selected pivot

User-requested feature (2026-07-18): pivot a bone around its PARENT's position
so the bone orbits instead of spinning in place — "something that has never
really been done correctly": in existing tools, repeated orbit rotations
compound until the bone "spurts out, moves far off screen to infinity".

## Why existing tools explode (failure analysis)

The blow-up is a property of the COMPUTATION STRUCTURE, not the values:

1. **Output feeds input.** The common drag loop computes each frame's delta
   against the LIVE bone transform — which already contains the previous
   frame's write PLUS whatever the engine did in between (Havok recompute,
   physics tick, propagation ordering, scale application). For own-pivot
   rotation the error is small and rotational; for an orbit the POSITION error
   is multiplicative in the radius: radius(n) = radius(0)·k^n. Even k = 1.001
   (0.1% per frame) reaches 7× in 2000 frames — "off to infinity" in seconds
   of dragging.
2. **Quaternion denormalization scales positions.** A rotation with norm
   1+e applied to an offset scales it by (1+e)^2 per application; incremental
   composition without normalization compounds it.
3. **Delta stacks accumulate increments.** Our (Brio-derived) BonePoseInfo
   accumulates per-frame increments into a stack; any bad increment is
   PERMANENT and built upon. (This matches the Anamnesis stability finding:
   Anamnesis avoids the class by making posing a function of a snapshot, not
   of the previous output — see anamnesis-audit.md.)

The game itself "works fine" because its animation pipeline derives every pose
from authored data as a function of TIME — never from last frame's output.
The fix is restoring exactly that property to the drag loop.

## The pivot model (PBI-002)

There is no separate "Orbit" feature. Rotation has exactly one visible pivot
choice, presented as a compact selector in the top transform toolbar
immediately after Local/World:

| Choice | Behavior |
|---|---|
| **Self** | Rotate at the effective primary target using the active Local/World orientation. The bone's position does not change. |
| **Parent** | Rotate around the parent's model-space position (frozen at gesture begin) using the **parent→child radial frame**: red points along normalized `child − parent`, the remaining axes are a stable orthonormal basis, and the visible frame follows the child as it orbits. The parent bone's own orientation is not the frame source. |
| **Selection** | Rotate the selected targets around the multi-selection centroid (frozen at gesture begin) with the active Local/World orientation — distinct from Parent. |

- The selector is visible only where the pivot changes the active transform
  meaning: the Rotate tool with a bone selection. Actor targets and the
  Translate/Scale tools do not show it.
- Parent is unavailable (disabled) when the effective transform primary has
  no valid parent in the current skeleton descriptor.
- The editor state holds one value: `RotationPivot { Self, Parent, Selection }`.
  The former inspector Orbit switch, the Parent/Selection/Custom segmented
  control, the Custom X/Y/Z rows, `IEditorState.OrbitBoneRotation`,
  `IEditorState.OrbitPivot`, `IEditorState.CustomOrbitPivot`, and the
  standalone `OrbitPivotMode` enum are deleted. There is no user-facing
  custom pivot; `PivotMode.Custom` in the application gesture service remains
  as the internal mechanism that carries the frozen pivot point.

## The design (frozen clean gesture)

Pivoted rotation is not a second transform system: it is the ordinary
`TransformGestureService` gesture with a pivot frozen at Begin.

- With Parent or Selection active and the Rotate tool selected, the gizmo
  begins a clean gesture whose `PivotMode` is `Custom` and whose pivot point
  freezes at pointer-down (parent model-space position through the viewport
  projection, or the frozen centroid of the effective roots). The gesture
  space is World.
- Every frame converts the manipulated matrix into a TOTAL delta from the
  frozen Begin baseline and dispatches `Update`; the service recomputes every
  target from its immutable captured state. No frame's output is any frame's
  input, so the radius cannot compound — idempotence by construction.
- **The gizmo is drawn at the pivot it rotates around.** At rest with Parent
  or Selection active, the gizmo's visible center sits at the current parent
  position or effective-root centroid; during a drag it stays at the frozen
  pivot while the bone orbits. With Self it sits on the bone as before. The
  manipulation matrix combines the pivot position with the primary target's
  rotation so Local axes remain meaningful.
- Changing pivot, tool, Local/World, or selection during a gesture cancels
  once, restores the frozen baseline once, and does not restart until the
  pointer is released. Commit writes one history patch; Escape writes none.

The former `OrbitSession`/`OrbitMath` strategy machinery (Snapshot / Rebase /
Live comparison modes) is deleted; the snapshot-absolute computation is the
only production path.

## Limitations (deliberate)

The inspector rail's rotation gizmo and numeric wells always rotate in place;
the pivot selector governs the in-world gizmo. Symmetry pairs and IK
participate exactly as in any other gesture (they are explicit targets or
session state of the same gesture). Undo restores through the normal
`TransformHistory` patch.
