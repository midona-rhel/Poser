# Orbit rotation — rotating a bone around its parent (or any pivot)

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
   of dragging. Encoded as a regression test:
   `OrbitMathTests.BugReproduction_LiveIncrementalDivergesWhereSnapshotDoesNot`.
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

## The design (frozen clean gesture)

Orbit is not a second transform system: it is the ordinary
`TransformGestureService` gesture with a pivot frozen at Begin.

- With Orbit enabled and the Rotate tool active, the gizmo begins a clean
  gesture whose `PivotMode` is `Custom` and whose pivot point freezes at
  pointer-down (parent position, frozen selection centroid, or the
  user-supplied point). The gesture space is World.
- Every frame converts the manipulated matrix into a TOTAL delta from the
  frozen Begin baseline and dispatches `Update`; the service recomputes every
  target from its immutable captured state. No frame's output is any frame's
  input, so the radius cannot compound — the same idempotence-by-construction
  property the analysis above demands, now provided by the single gesture
  path instead of a dedicated orbit session.
- The gizmo adjusts only its own presentation baseline for pivot rotation;
  it retains no native baselines and no per-bone state.
- Escape, tool/orientation change, selection change, and scene invalidation
  cancel the gesture exactly once and restore the frozen baseline; commit
  writes one history patch.

The former `OrbitSession`/`OrbitMath` strategy machinery (Snapshot / Rebase /
Live comparison modes) is deleted; the snapshot-absolute computation is the
only production path.

## Pivots

`Poser.Core.OrbitPivotMode`: **Parent** (headline; parent bone's model-space
position read through the viewport projection at Begin), **SelectionCenter**
(frozen centroid of the effective transform targets — group orbits),
**Custom** (user-supplied world-space point).

## Limitations (deliberate)

Symmetry pairs and IK participate exactly as in any other gesture (they are
explicit targets or session state of the same gesture). Undo restores through
the normal `TransformHistory` patch.
