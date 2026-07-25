# Expression, gaze, and IK

## Expression

Ktisis v0.4.0.0 per-race action-unit catalogs as sliders; no catalog for a
customize combination = quiet unavailable state, never another race's data.
Deltas are head-relative and pre-multiplied (Ktisis-verified); blending owns
one named `expression` layer per bone, replaced on recompute and removed at
identity — 0→1→0 restores exactly and manual face stacks are never touched.
Bones resolve by `(name, partialId)` with evaluated partials (≥ 1) winning
over partial-0 duplicates; unresolvable units are hidden. No propagation.

## Gaze

Modes Off/Forward/Camera/Actor drive Eyes, Head, and Body independently;
each participating part can be locked at its current target. Actor mode
requires an explicit target choice: the UI picker lists candidates by stable
`ActorId` from the scene snapshot, while `GazeService` keys its state and
target by native `GameObjectId`. Disabling a part immediately restores its
pre-Poser native target (captured once, never re-seeded from Poser output);
Off writes nothing. Redraws cannot orphan the id-keyed state; a vanished
target transitions to Off. Reset restores every part and clears gaze state.

## IK

Calls the game's own Havok solvers (Brio) — engine-identical results, applied
live during pose application: an armed bone's translation delta becomes a
solver target; rotation/scale never solve. The solved chain is never baked —
undo and export stay pure deltas — but per-bone arming state persists for the
session. Arming is per selected bone via `ConfigureIk`, gated to the four
chain ends (`j_te_l/r`, `j_asi_d_l/r`); no actor-wide arming exists.
