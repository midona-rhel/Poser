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
live during pose application: an armed chain's translation delta becomes the
solver target; rotation/scale never start a solve. The solved chain is never
baked — undo and export stay pure deltas — while per-chain configuration
persists for the session, keyed by the exact skeleton instance (a replacement
never inherits it). The four endpoints (`j_te_l/r`, `j_asi_d_l/r`) resolve
their Ktisis chains inside their own slot; no actor-wide arming exists.

- One validated `IkChainConfig` per chain carries BOTH solver settings
  (switching never discards tuning); invalid values never reach the native
  boundary, and changes are rejected during an active gesture. All reads and
  writes go through the stable-id `IIkConfigurationPort`.
- Two Joint (offered only when its mandatory chain resolves): Relative
  target follows animation; Fixed captures `(effective target, authored
  translation)` and later targets `capture + (delta − captured delta)`, so
  mode changes never jump and undo/redo still moves the target. Joint
  gains, cosine-converted hinge limits, normalized axis, twist bones, and
  optional end-rotation enforcement (never applied a second time). CCD:
  depth clamped to the chain, iterations, configured gain.
- Disabling keeps tuning but clears the fixed capture; Reset Defaults
  restores chain defaults preserving Enabled; Reset Bone keeps IK; Reset
  All disables and clears every chain. Defaults reproduce the pre-advanced
  Live IK behavior exactly.
