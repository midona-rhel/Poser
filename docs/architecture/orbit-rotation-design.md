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

## The design (three strategies, switchable in game)

`Poser.Core.OrbitMath` (pure) + `OrbitSession` (drag transaction) +
`IBonePosingService.BeginOrbitSession`. Gizmo wiring: with orbit enabled and
the Rotate operation, the drag creates a session at drag start; the gizmo is
fed the session's pure-math target (never live memory); each frame's gizmo
delta composes into a running TOTAL rotation; the session evaluates targets
from the immutable snapshot.

- **SnapshotAbsolute (default)** — target = pivot + R_total·(base − pivot),
  rot = R_total·base_rot, everything derived from the drag-start snapshot; the
  session's stack contribution is REPLACED each frame
  (`BonePoseInfo.SetStackTransform`), not accumulated. Idempotent by
  construction: error cannot compound because no frame's output is any
  frame's input. Radius preserved exactly (normalization inside Evaluate).
- **PureIncrementalRebase** — increments still accumulate in the stack, but
  each increment is computed between two exact snapshot evaluations. Additive
  float error only. Exists for comparison.
- **LiveIncremental (control)** — deliberately reproduces the broken
  structure (per-frame base = live transform) so the difference can be
  DEMONSTRATED in game. Clearly labeled in the command output.

Safety net regardless of strategy: `OrbitMath.IsSane` (NaN/Inf/magnitude
guard) — a failed frame is dropped and counted (`RejectedFrames`), never
written. A bone can lag a frame; it can never fly away.

## Pivots

`OrbitPivotMode`: **Parent** (headline; parent bone's model-space position),
**SelectionCenter** (centroid — group orbits), **Custom** (reserved for the
user-placed pivot-point entity, `EntityType.PivotPoint`, when its UI lands).

## Usage (until the pane control lands)

`/poser orbit on` · `/poser orbit pivot parent|center` ·
`/poser orbit strategy snapshot|rebase|live` — then rotate-drag as usual.

## v1 limitations (deliberate)

Symmetry pairs and IK do not participate in orbit drags (plain rotations
only); undo works through the normal drag-session history events. Local-space
authoring (writing the orbit as local pose so Havok propagates it) is a
possible fourth strategy if model-space writing shows interference in game —
tracked in the checklist notes.

## Verification

The live orbit scenarios cover radius preservation, idempotence, full-circle,
denormalized-input resistance, repeated-step boundedness, the divergence repro,
and the sanity guard. See P-STAB item 9.
